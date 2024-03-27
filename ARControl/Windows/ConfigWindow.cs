using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using ARControl.External;
using ARControl.GameData;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.ImGuiMethods;
using ImGuiNET;
using LLib;
using LLib.ImGui;

namespace ARControl.Windows;

internal sealed class ConfigWindow : LWindow
{
    // TODO This should also allow retainers under max level
    private const byte MinLevel = 10;

    private static readonly Vector4 ColorGreen = ImGuiColors.HealerGreen;
    private static readonly Vector4 ColorRed = ImGuiColors.DalamudRed;
    private static readonly Vector4 ColorGrey = ImGuiColors.DalamudGrey;
    private static readonly string[] StockingTypeLabels = ["Collect Once", "Keep in Stock"];

    private static readonly string[] PriorityLabels =
        { "Collect in order of the list", "Collect item with lowest inventory first" };

    private static readonly Regex CountAndName = new(@"^(\d{1,5})x?\s+(.*)$", RegexOptions.Compiled);
    private static readonly string CurrentCharPrefix = FontAwesomeIcon.Male.ToIconString();
    private static readonly string DiscardWarningPrefix = FontAwesomeIcon.ExclamationCircle.ToIconString();

    private readonly DalamudPluginInterface _pluginInterface;
    private readonly Configuration _configuration;
    private readonly GameCache _gameCache;
    private readonly IClientState _clientState;
    private readonly ICommandManager _commandManager;
    private readonly IconCache _iconCache;
    private readonly DiscardHelperIpc _discardHelperIpc;
    private readonly IPluginLog _pluginLog;

    private readonly Dictionary<Guid, TemporaryConfig> _currentEditPopups = new();
    private string _searchString = string.Empty;
    private float _mainIndentSize = 1;
    private TemporaryConfig _newGroup = new() { Name = string.Empty };

    private TemporaryConfig _newList = new()
    {
        Name = string.Empty,
        ListType = Configuration.ListType.CollectOneTime,
        ListPriority = Configuration.ListPriority.InOrder,
        CheckRetainerInventory = false,
    };

    public ConfigWindow(
        DalamudPluginInterface pluginInterface,
        Configuration configuration,
        GameCache gameCache,
        IClientState clientState,
        ICommandManager commandManager,
        IconCache iconCache,
        DiscardHelperIpc discardHelperIpc,
        IPluginLog pluginLog)
        : base($"ARC {SeIconChar.Collectible.ToIconString()}###ARControlConfig")
    {
        _pluginInterface = pluginInterface;
        _configuration = configuration;
        _gameCache = gameCache;
        _clientState = clientState;
        _commandManager = commandManager;
        _iconCache = iconCache;
        _discardHelperIpc = discardHelperIpc;
        _pluginLog = pluginLog;

        SizeConstraints = new()
        {
            MinimumSize = new Vector2(480, 300),
            MaximumSize = new Vector2(9999, 9999),
        };
    }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("ARConfigTabs"))
        {
            ImGui.PushFont(UiBuilder.IconFont);
            _mainIndentSize = ImGui.CalcTextSize(FontAwesomeIcon.Cog.ToIconString()).X +
                ImGui.GetStyle().FramePadding.X * 2f +
                ImGui.GetStyle().ItemSpacing.X - ImGui.GetStyle().WindowPadding.X / 2;
            ImGui.PopFont();

            DrawVentureLists();
            DrawCharacterGroups();
            DrawCharacters();
            DrawGatheredItemsToCheck();
            DrawMiscTab();

            ImGui.EndTabBar();
        }
    }

    private void DrawVentureLists()
    {
        if (ImGui.BeginTabItem("Venture Lists###TabVentureLists"))
        {
            Configuration.ItemList? listToDelete = null;
            IReadOnlySet<uint> itemsToDiscard = _discardHelperIpc.GetItemsToDiscard();
            foreach (var list in _configuration.ItemLists)
            {
                ImGui.PushID($"List{list.Id}");

                if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog))
                {
                    _currentEditPopups[list.Id] = new TemporaryConfig
                    {
                        Name = list.Name,
                        ListType = list.Type,
                        ListPriority = list.Priority,
                        CheckRetainerInventory = list.CheckRetainerInventory,
                    };
                    ImGui.OpenPopup($"##EditList{list.Id}");
                }

                DrawVentureListEditorPopup(list, ref listToDelete);

                ImGui.SameLine();

                string label = $"{list.Name} {list.GetIcon()}";

                if (ImGui.CollapsingHeader(label))
                {
                    ImGui.Indent(_mainIndentSize);
                    DrawVentureListItemSelection(list, itemsToDiscard);
                    ImGui.Unindent(_mainIndentSize);
                }

                ImGui.PopID();
            }

            if (listToDelete != null)
            {
                _configuration.ItemLists.Remove(listToDelete);
                Save();
            }

            ImGui.Separator();
            DrawNewVentureList();
            ImGui.EndTabItem();
        }
    }

    private void DrawVentureListEditorPopup(Configuration.ItemList list, ref Configuration.ItemList? listToDelete)
    {
        var assignedCharacters = _configuration.Characters
            .Where(x => x.Type == Configuration.CharacterType.Standalone && x.ItemListIds.Contains(list.Id))
            .OrderBy(x => x.WorldName)
            .ThenBy(x => x.LocalContentId)
            .ToList();
        var assignedGroups = _configuration.CharacterGroups
            .Where(x => x.ItemListIds.Contains(list.Id))
            .ToList();
        if (_currentEditPopups.TryGetValue(list.Id, out TemporaryConfig? temporaryConfig) &&
            ImGui.BeginPopup($"##EditList{list.Id}"))
        {
            var (save, canSave) = DrawVentureListEditor(temporaryConfig, list);
            ImGui.BeginDisabled(!canSave || (list.Name == temporaryConfig.Name &&
                                             list.Type == temporaryConfig.ListType &&
                                             list.Priority == temporaryConfig.ListPriority &&
                                             list.CheckRetainerInventory == temporaryConfig.CheckRetainerInventory));
            save |= ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Save, "Save");
            ImGui.EndDisabled();

            if (save && canSave)
            {
                list.Name = temporaryConfig.Name;
                list.Type = temporaryConfig.ListType;

                if (list.Type == Configuration.ListType.CollectOneTime)
                {
                    list.Priority = Configuration.ListPriority.InOrder;
                    list.CheckRetainerInventory = false;
                }
                else
                {
                    list.Priority = temporaryConfig.ListPriority;
                    list.CheckRetainerInventory = temporaryConfig.CheckRetainerInventory;
                }

                ImGui.CloseCurrentPopup();
                Save();
            }
            else
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(assignedCharacters.Count > 0 || assignedGroups.Count > 0);
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Times, "Delete"))
                {
                    listToDelete = list;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndDisabled();
                if ((assignedCharacters.Count > 0 || assignedGroups.Count > 0) &&
                    ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.BeginTooltip();
                    ImGui.Text(
                        $"Remove this list from the {assignedCharacters.Count} character(s) and {assignedGroups.Count} group(s) using it before deleting it.");
                    foreach (var character in assignedCharacters)
                        ImGui.BulletText($"{character.CharacterName} @ {character.WorldName}");
                    foreach (var group in assignedGroups)
                        ImGui.BulletText($"{group.Name}");
                    ImGui.EndTooltip();
                }
            }

            ImGui.EndPopup();
        }
    }

    private void DrawNewVentureList()
    {
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "Add Venture List"))
            ImGui.OpenPopup("##AddList");

        if (ImGui.BeginPopup("##AddList"))
        {
            (bool save, bool canSave) = DrawVentureListEditor(_newList, null);

            ImGui.BeginDisabled(!canSave);
            save |= ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Save, "Save");
            ImGui.EndDisabled();

            if (save && canSave)
            {
                _configuration.ItemLists.Add(new Configuration.ItemList
                {
                    Id = Guid.NewGuid(),
                    Name = _newList.Name,
                    Type = _newList.ListType,
                    Priority = _newList.ListPriority,
                    CheckRetainerInventory = _newList.CheckRetainerInventory,
                });

                _newList = new()
                {
                    Name = string.Empty,
                    ListType = Configuration.ListType.CollectOneTime,
                    ListPriority = Configuration.ListPriority.InOrder,
                    CheckRetainerInventory = false,
                };

                ImGui.CloseCurrentPopup();
                Save();
            }

            ImGui.EndPopup();
        }
    }

    private (bool Save, bool CanSave) DrawVentureListEditor(TemporaryConfig temporaryConfig,
        Configuration.ItemList? list)
    {
        ImGui.SetNextItemWidth(375 * ImGuiHelpers.GlobalScale);
        string listName = temporaryConfig.Name;
        bool save = ImGui.InputTextWithHint("", "List Name...", ref listName, 64,
            ImGuiInputTextFlags.EnterReturnsTrue);
        bool canSave = IsValidListName(listName, list);
        temporaryConfig.Name = listName;

        ImGui.PushID($"Type{list?.Id ?? Guid.Empty}");
        ImGui.SetNextItemWidth(375 * ImGuiHelpers.GlobalScale);
        int type = (int)temporaryConfig.ListType;
        if (ImGui.Combo("", ref type, StockingTypeLabels, StockingTypeLabels.Length))
        {
            temporaryConfig.ListType = (Configuration.ListType)type;
            if (temporaryConfig.ListType == Configuration.ListType.CollectOneTime)
                temporaryConfig.ListPriority = Configuration.ListPriority.InOrder;
        }

        ImGui.PopID();

        if (temporaryConfig.ListType == Configuration.ListType.KeepStocked)
        {
            ImGui.PushID($"Priority{list?.Id ?? Guid.Empty}");
            ImGui.SetNextItemWidth(375 * ImGuiHelpers.GlobalScale);
            int priority = (int)temporaryConfig.ListPriority;
            if (ImGui.Combo("", ref priority, PriorityLabels, PriorityLabels.Length))
                temporaryConfig.ListPriority = (Configuration.ListPriority)priority;
            ImGui.PopID();

            ImGui.PushID($"CheckRetainerInventory{list?.Id ?? Guid.Empty}");
            bool checkRetainerInventory = temporaryConfig.CheckRetainerInventory;
            if (ImGui.Checkbox("Check Retainer Inventory for items (requires AllaganTools)",
                    ref checkRetainerInventory))
                temporaryConfig.CheckRetainerInventory = checkRetainerInventory;
            ImGui.PopID();
        }

        return (save, canSave);
    }

    private void DrawVentureListItemSelection(Configuration.ItemList list, IReadOnlySet<uint> itemsToDiscard)
    {
        ImGuiEx.SetNextItemFullWidth();
        if (ImGui.BeginCombo($"##VentureSelection{list.Id}", "Add Venture...", ImGuiComboFlags.HeightLarge))
        {
            ImGuiEx.SetNextItemFullWidth();

            bool addFirst = ImGui.InputTextWithHint("", "Filter...", ref _searchString, 256,
                ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue);

            int quantity;
            string itemName;
            var regexMatch = CountAndName.Match(_searchString);
            if (regexMatch.Success)
            {
                quantity = int.Parse(regexMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                itemName = regexMatch.Groups[2].Value;
            }
            else
            {
                quantity = 0;
                itemName = _searchString;
            }

            foreach (var filtered in _gameCache.Ventures
                         .Where(x => x.Name.Contains(itemName, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(x => x.Level)
                         .ThenBy(x => x.Name)
                         .ThenBy(x => x.ItemId)
                         .GroupBy(x => x.ItemId)
                         .Select(x => new
                         {
                             Venture = x.First(),
                             CategoryNames = x.Select(y => y.CategoryName)
                         }))
            {
                IDalamudTextureWrap? icon = _iconCache.GetIcon(filtered.Venture.IconId);
                if (icon != null)
                {
                    ImGui.Image(icon.ImGuiHandle, new Vector2(23, 23));
                    ImGui.SameLine();
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3);
                }

                bool addThis = ImGui.Selectable(
                    $"{filtered.Venture.Name} ({string.Join(" ", filtered.CategoryNames)})##SelectVenture{filtered.Venture.RowId}");

                if (addThis || addFirst)
                {
                    list.Items.Add(new Configuration.QueuedItem
                    {
                        ItemId = filtered.Venture.ItemId,
                        RemainingQuantity = quantity,
                    });

                    if (addFirst)
                    {
                        addFirst = false;
                        ImGui.CloseCurrentPopup();
                    }

                    Save();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.Spacing();

        Configuration.QueuedItem? itemToRemove = null;
        Configuration.QueuedItem? itemToAdd = null;
        int indexToAdd = 0;
        for (int i = 0; i < list.Items.Count; ++i)
        {
            var item = list.Items[i];
            ImGui.PushID($"QueueItem{i}");
            var ventures = _gameCache.Ventures.Where(x => x.ItemId == item.ItemId).ToList();
            var venture = ventures.First();

            if (itemsToDiscard.Contains(venture.ItemId))
            {
                ImGui.PushFont(UiBuilder.IconFont);
                var pos = ImGui.GetCursorPos();
                ImGui.SetCursorPos(new Vector2(pos.X - ImGui.CalcTextSize(DiscardWarningPrefix).X - 5, pos.Y + 2));
                ImGui.TextColored(ImGuiColors.DalamudYellow, DiscardWarningPrefix);
                ImGui.SetCursorPos(pos);
                ImGui.PopFont();

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("This item will be automatically discarded by 'Discard Helper'.");
            }

            IDalamudTextureWrap? icon = _iconCache.GetIcon(venture.IconId);
            if (icon != null)
            {
                ImGui.Image(icon.ImGuiHandle, new Vector2(ImGui.GetFrameHeight()));
                ImGui.SameLine(0, ImGui.GetStyle().FramePadding.X);
            }

            ImGui.SetNextItemWidth(130 * ImGuiHelpers.GlobalScale);
            int quantity = item.RemainingQuantity;
            if (ImGui.InputInt($"{venture.Name} ({string.Join(" ", ventures.Select(x => x.CategoryName))})",
                    ref quantity, 100))
            {
                item.RemainingQuantity = quantity;
                Save();
            }

            if (list.Items.Count > 1)
            {
                bool wrap = _configuration.ConfigUiOptions.WrapAroundWhenReordering;

                ImGui.PushFont(UiBuilder.IconFont);
                ImGui.SameLine(ImGui.GetContentRegionAvail().X +
                               _mainIndentSize +
                               ImGui.GetStyle().WindowPadding.X -
                               ImGui.CalcTextSize(FontAwesomeIcon.ArrowUp.ToIconString()).X -
                               ImGui.CalcTextSize(FontAwesomeIcon.ArrowDown.ToIconString()).X -
                               ImGui.CalcTextSize(FontAwesomeIcon.Times.ToIconString()).X -
                               ImGui.GetStyle().FramePadding.X * 6 -
                               ImGui.GetStyle().ItemSpacing.X);
                ImGui.PopFont();
                ImGui.BeginDisabled(i == 0 && !wrap);
                if (ImGuiComponents.IconButton($"##Up{i}", FontAwesomeIcon.ArrowUp))
                {
                    itemToAdd = item;
                    if (i > 0)
                        indexToAdd = i - 1;
                    else
                        indexToAdd = list.Items.Count - 1;
                }

                ImGui.EndDisabled();

                ImGui.SameLine(0, 0);
                ImGui.BeginDisabled(i == list.Items.Count - 1 && !wrap);
                if (ImGuiComponents.IconButton($"##Down{i}", FontAwesomeIcon.ArrowDown))
                {
                    itemToAdd = item;
                    if (i < list.Items.Count - 1)
                        indexToAdd = i + 1;
                    else
                        indexToAdd = 0;
                }

                ImGui.EndDisabled();
                ImGui.SameLine();
            }
            else
            {
                ImGui.PushFont(UiBuilder.IconFont);
                ImGui.SameLine(ImGui.GetContentRegionAvail().X +
                               _mainIndentSize +
                               ImGui.GetStyle().WindowPadding.X -
                               ImGui.CalcTextSize(FontAwesomeIcon.Times.ToIconString()).X -
                               ImGui.GetStyle().FramePadding.X * 2);
                ImGui.PopFont();
            }

            if (ImGuiComponents.IconButton($"##Remove{i}", FontAwesomeIcon.Times))
                itemToRemove = item;

            ImGui.PopID();
        }

        if (itemToRemove != null)
        {
            list.Items.Remove(itemToRemove);
            Save();
        }

        if (itemToAdd != null)
        {
            _pluginLog.Information($"Updating {itemToAdd.ItemId} → {indexToAdd}");
            list.Items.Remove(itemToAdd);
            list.Items.Insert(indexToAdd, itemToAdd);
            Save();
        }

        ImGui.Spacing();
        List<Configuration.QueuedItem> clipboardItems = ParseClipboardItems();
        ImportFromClipboardButton(list, clipboardItems);
        RemoveFinishedItemsButton(list);

        ImGui.Spacing();
    }

    private void ImportFromClipboardButton(Configuration.ItemList list, List<Configuration.QueuedItem> clipboardItems)
    {
        ImGui.BeginDisabled(clipboardItems.Count == 0);
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Download, "Import from Clipboard"))
        {
            _pluginLog.Information($"Importing {clipboardItems.Count} clipboard items");
            foreach (var item in clipboardItems)
            {
                var existingItem = list.Items.FirstOrDefault(x => x.ItemId == item.ItemId);
                if (existingItem != null)
                    existingItem.RemainingQuantity += item.RemainingQuantity;
                else
                    list.Items.Add(item);
            }

            ImGui.SetClipboardText(null);
            Save();
        }

        ImGui.EndDisabled();

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text("Supports importing a list in a Teamcraft-compatible format.");
            ImGui.Spacing();
            if (clipboardItems.Count > 0)
            {
                ImGui.Text("Clicking this button now would add the following items:");
                ImGui.Indent();
                foreach (var item in clipboardItems)
                    ImGui.TextUnformatted(
                        $"{item.RemainingQuantity}x {_gameCache.Ventures.First(x => item.ItemId == x.ItemId).Name}");
                ImGui.Unindent();
            }
            else
            {
                ImGui.Text("For example:");
                ImGui.Indent();
                ImGui.Text("2000x Cobalt Ore");
                ImGui.Text("1000x Gold Ore");
                ImGui.Unindent();
            }

            ImGui.EndTooltip();
        }
    }

    private void RemoveFinishedItemsButton(Configuration.ItemList list)
    {
        if (list.Items.Count > 0 && list.Type == Configuration.ListType.CollectOneTime)
        {
            ImGui.SameLine();
            ImGui.BeginDisabled(list.Items.All(x => x.RemainingQuantity > 0));
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Check, "Remove all finished items"))
            {
                list.Items.RemoveAll(q => q.RemainingQuantity <= 0);
                Save();
            }

            ImGui.EndDisabled();
        }
    }

    private List<Configuration.QueuedItem> ParseClipboardItems()
    {
        List<Configuration.QueuedItem> clipboardItems = new List<Configuration.QueuedItem>();
        try
        {
            string? clipboardText = GetClipboardText();
            if (!string.IsNullOrWhiteSpace(clipboardText))
            {
                foreach (var clipboardLine in clipboardText.ReplaceLineEndings().Split(Environment.NewLine))
                {
                    var match = CountAndName.Match(clipboardLine);
                    if (!match.Success)
                        continue;

                    var venture = _gameCache.Ventures.FirstOrDefault(x =>
                        x.Name.Equals(match.Groups[2].Value, StringComparison.OrdinalIgnoreCase));
                    if (venture != null && int.TryParse(match.Groups[1].Value, out int quantity))
                    {
                        clipboardItems.Add(new Configuration.QueuedItem
                        {
                            ItemId = venture.ItemId,
                            RemainingQuantity = quantity,
                        });
                    }
                }
            }
        }
        catch (Exception e)
        {
            _pluginLog.Warning(e, "Unable to extract clipboard text");
        }

        return clipboardItems;
    }

    private void DrawCharacters()
    {
        if (ImGui.BeginTabItem("Retainers###TabRetainers"))
        {
            foreach (var world in _configuration.Characters
                         .Where(x => x.Retainers.Any(y => y.Job != 0))
                         .OrderBy(x => x.LocalContentId)
                         .GroupBy(x => x.WorldName))
            {
                ImGui.CollapsingHeader(world.Key,
                    ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.Bullet);
                foreach (var character in world)
                {
                    ImGui.PushID($"Char{character.LocalContentId}");

                    ImGui.SetNextItemWidth(ImGui.GetFontSize() * 30);
                    Vector4 buttonColor = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.FrameBg));
                    if (character is { Type: not Configuration.CharacterType.NotManaged, Retainers.Count: > 0 })
                    {
                        if (character.Retainers.All(x => x.Managed))
                            buttonColor = ImGuiColors.HealerGreen;
                        else if (character.Retainers.All(x => !x.Managed))
                            buttonColor = ImGuiColors.DalamudRed;
                        else
                            buttonColor = ImGuiColors.DalamudOrange;
                    }

                    if (ImGuiComponents.IconButton(FontAwesomeIcon.Book, buttonColor))
                    {
                        if (character.Type == Configuration.CharacterType.NotManaged)
                        {
                            character.Type = Configuration.CharacterType.Standalone;
                            character.CharacterGroupId = Guid.Empty;
                        }
                        else
                        {
                            character.Type = Configuration.CharacterType.NotManaged;
                            character.CharacterGroupId = Guid.Empty;
                        }

                        Save();
                    }

                    ImGui.SameLine();

                    if (ImGui.CollapsingHeader(
                            $"{character.CharacterName} {(character.Type != Configuration.CharacterType.NotManaged ? $"({character.Retainers.Count(x => x.Managed)} / {character.Retainers.Count})" : "")}###{character.LocalContentId}"))
                    {
                        ImGui.Indent(_mainIndentSize);

                        List<(Guid Id, string Name)> groups =
                            new List<(Guid Id, string Name)> { (Guid.Empty, "No Group (manually assign lists)") }
                                .Concat(_configuration.CharacterGroups.Select(x => (x.Id, x.Name)))
                                .ToList();


                        if (ImGui.BeginTabBar("CharOptions"))
                        {
                            if (character.Type != Configuration.CharacterType.NotManaged &&
                                ImGui.BeginTabItem("Venture Lists"))
                            {
                                int groupIndex = 0;
                                if (character.Type == Configuration.CharacterType.PartOfCharacterGroup)
                                    groupIndex = groups.FindIndex(x => x.Id == character.CharacterGroupId);
                                if (ImGui.Combo("Character Group", ref groupIndex, groups.Select(x => x.Name).ToArray(),
                                        groups.Count))
                                {
                                    if (groupIndex == 0)
                                    {
                                        character.Type = Configuration.CharacterType.Standalone;
                                        character.CharacterGroupId = Guid.Empty;
                                    }
                                    else
                                    {
                                        character.Type = Configuration.CharacterType.PartOfCharacterGroup;
                                        character.CharacterGroupId = groups[groupIndex].Id;
                                    }

                                    Save();
                                }

                                ImGui.Separator();
                                if (groupIndex == 0)
                                {
                                    // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
                                    character.ItemListIds ??= new();
                                    DrawVentureListSelection(
                                        character.LocalContentId.ToString(CultureInfo.InvariantCulture),
                                        character.ItemListIds);
                                }
                                else
                                {
                                    ImGui.TextWrapped($"Retainers will participate in the following lists:");
                                    ImGui.Indent(_mainIndentSize);

                                    var group = _configuration.CharacterGroups.Single(
                                        x => x.Id == groups[groupIndex].Id);
                                    var lists = group.ItemListIds
                                        .Where(listId => listId != Guid.Empty)
                                        .Select(listId => _configuration.ItemLists.SingleOrDefault(x => x.Id == listId))
                                        .Where(list => list != null)
                                        .Cast<Configuration.ItemList>()
                                        .ToList();
                                    if (lists.Count > 0)
                                    {
                                        foreach (var list in lists)
                                            ImGui.BulletText($"{list.Name}");
                                    }
                                    else
                                        ImGui.TextColored(ImGuiColors.DalamudRed, "(None)");

                                    ImGui.Unindent(_mainIndentSize);
                                    ImGui.Spacing();
                                }

                                ImGui.EndTabItem();
                            }

                            if (ImGui.BeginTabItem("Retainers"))
                            {
                                foreach (var retainer in character.Retainers.Where(x => x.Job > 0)
                                             .OrderBy(x => x.DisplayOrder)
                                             .ThenBy(x => x.RetainerContentId))
                                {
                                    ImGui.BeginDisabled(retainer.Level < MinLevel);

                                    bool managed = retainer is { Managed: true, Level: >= MinLevel };

                                    IDalamudTextureWrap? icon = _iconCache.GetIcon(62000 + retainer.Job);
                                    if (icon != null)
                                    {
                                        ImGui.Image(icon.ImGuiHandle, new Vector2(ImGui.GetFrameHeight()));
                                        ImGui.SameLine(0, ImGui.GetStyle().FramePadding.X);
                                    }

                                    if (ImGui.Checkbox(
                                            $"{retainer.Name}###Retainer{retainer.Name}{retainer.RetainerContentId}",
                                            ref managed))
                                    {
                                        retainer.Managed = managed;
                                        Save();
                                    }

                                    ImGui.EndDisabled();
                                }

                                ImGui.EndTabItem();
                            }

                            ImGui.EndTabBar();
                        }


                        ImGui.Unindent(_mainIndentSize);
                    }

                    ImGui.PopID();
                }
            }

            ImGui.EndTabItem();
        }
    }

    private void DrawCharacterGroups()
    {
        if (ImGui.BeginTabItem("Groups###TabGroups"))
        {
            Configuration.CharacterGroup? groupToDelete = null;
            foreach (var group in _configuration.CharacterGroups)
            {
                ImGui.PushID($"##Group{group.Id}");

                if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog))
                {
                    _currentEditPopups[group.Id] = new TemporaryConfig
                    {
                        Name = group.Name,
                    };
                    ImGui.OpenPopup($"##EditGroup{group.Id}");
                }

                DrawCharacterGroupEditorPopup(group, out var assignedCharacters, ref groupToDelete);
                ImGui.SameLine();
                DrawCharacterGroup(group, assignedCharacters);

                ImGui.PopID();
            }

            if (groupToDelete != null)
            {
                _configuration.CharacterGroups.Remove(groupToDelete);
                Save();
            }

            ImGui.Separator();
            DrawNewCharacterGroup();
            ImGui.EndTabItem();
        }
    }

    private void DrawCharacterGroupEditorPopup(Configuration.CharacterGroup group,
        out List<Configuration.CharacterConfiguration> assignedCharacters,
        ref Configuration.CharacterGroup? groupToDelete)
    {
        assignedCharacters = _configuration.Characters
            .Where(x => x.Type == Configuration.CharacterType.PartOfCharacterGroup &&
                        x.CharacterGroupId == group.Id)
            .OrderBy(x => x.WorldName)
            .ThenBy(x => x.LocalContentId)
            .ToList();
        if (_currentEditPopups.TryGetValue(group.Id, out TemporaryConfig? temporaryConfig) &&
            ImGui.BeginPopup($"##EditGroup{group.Id}"))
        {
            (bool save, bool canSave) = DrawGroupEditor(temporaryConfig, group);

            ImGui.BeginDisabled(!canSave || group.Name == temporaryConfig.Name);
            save |= ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Save, "Save");
            ImGui.EndDisabled();

            if (save && canSave)
            {
                group.Name = temporaryConfig.Name;

                ImGui.CloseCurrentPopup();
                Save();
            }
            else
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(assignedCharacters.Count > 0);
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Times, "Delete"))
                {
                    groupToDelete = group;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndDisabled();
                if (assignedCharacters.Count > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.BeginTooltip();
                    ImGui.Text(
                        $"Remove the {assignedCharacters.Count} character(s) from this group before deleting it.");
                    foreach (var character in assignedCharacters)
                        ImGui.BulletText($"{character.CharacterName} @ {character.WorldName}");
                    ImGui.EndTooltip();
                }
            }

            ImGui.EndPopup();
        }
    }

    private void DrawCharacterGroup(Configuration.CharacterGroup group,
        List<Configuration.CharacterConfiguration> assignedCharacters)
    {
        string countLabel = assignedCharacters.Count == 0 ? "no characters"
            : assignedCharacters.Count == 1 ? "1 character"
            : $"{assignedCharacters.Count} characters";
        if (ImGui.CollapsingHeader($"{group.Name} ({countLabel})"))
        {
            ImGui.Indent(_mainIndentSize);
            if (ImGui.BeginTabBar("GroupOptions"))
            {
                if (ImGui.BeginTabItem("Venture Lists"))
                {
                    DrawVentureListSelection(group.Id.ToString(), group.ItemListIds);
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Characters"))
                {
                    ImGui.Text("Characters in this group:");
                    ImGui.Indent(_mainIndentSize);
                    foreach (var character in assignedCharacters.OrderBy(x => x.WorldName)
                                 .ThenBy(x => x.LocalContentId))
                        ImGui.TextUnformatted($"{character.CharacterName} @ {character.WorldName}");
                    ImGui.Unindent(_mainIndentSize);
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            ImGui.Unindent(_mainIndentSize);
        }
    }

    private void DrawNewCharacterGroup()
    {
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "Add Group"))
            ImGui.OpenPopup("##AddGroup");

        if (ImGui.BeginPopup("##AddGroup"))
        {
            (bool save, bool canSave) = DrawGroupEditor(_newGroup, null);

            ImGui.BeginDisabled(!canSave);
            save |= ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Save, "Save");
            ImGui.EndDisabled();

            if (save && canSave)
            {
                _configuration.CharacterGroups.Add(new Configuration.CharacterGroup
                {
                    Id = Guid.NewGuid(),
                    Name = _newGroup.Name,
                    ItemListIds = new(),
                });

                _newGroup = new() { Name = string.Empty };

                ImGui.CloseCurrentPopup();
                Save();
            }

            ImGui.EndPopup();
        }
    }

    private (bool Save, bool CanSave) DrawGroupEditor(TemporaryConfig group,
        Configuration.CharacterGroup? existingGroup)
    {
        string name = group.Name;
        bool save = ImGui.InputTextWithHint("", "Group Name...", ref name, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        bool canSave = IsValidGroupName(name, existingGroup);

        group.Name = name;
        return (save, canSave);
    }

    private bool IsValidGroupName(string name, Configuration.CharacterGroup? existingGroup)
    {
        return name.Length >= 2 &&
               !name.Contains('%', StringComparison.Ordinal) &&
               !_configuration.CharacterGroups.Any(x => x != existingGroup && name.EqualsIgnoreCase(x.Name));
    }

    private bool IsValidListName(string name, Configuration.ItemList? existingList)
    {
        return name.Length >= 2 &&
               !name.Contains('%', StringComparison.Ordinal) &&
               !_configuration.ItemLists.Any(x => x != existingList && name.EqualsIgnoreCase(x.Name));
    }

    private void DrawGatheredItemsToCheck()
    {
        if (ImGui.BeginTabItem("Locked Items###TabLockedItems"))
        {
            bool checkPerCharacter = _configuration.ConfigUiOptions.CheckGatheredItemsPerCharacter;
            if (ImGui.Checkbox("Group by character", ref checkPerCharacter))
            {
                _configuration.ConfigUiOptions.CheckGatheredItemsPerCharacter = checkPerCharacter;
                Save();
            }

            bool onlyShowMissing = _configuration.ConfigUiOptions.OnlyShowMissingGatheredItems;
            if (ImGui.Checkbox("Only show missing items", ref onlyShowMissing))
            {
                _configuration.ConfigUiOptions.OnlyShowMissingGatheredItems = onlyShowMissing;
                Save();
            }

            ImGui.Separator();

            var itemsToCheck =
                _configuration.ItemLists
                    .SelectMany(x => x.Items)
                    .Select(x => x.ItemId)
                    .Distinct()
                    .Select(itemId => new
                    {
                        GatheredItem = _gameCache.ItemsToGather.SingleOrDefault(x => x.ItemId == itemId),
                        Ventures = _gameCache.Ventures.Where(x => x.ItemId == itemId).ToList()
                    })
                    .Where(x => x.GatheredItem != null && x.Ventures.Count > 0)
                    .Select(x => new CheckedItem
                    {
                        GatheredItem = x.GatheredItem!,
                        Ventures = x.Ventures,
                        ItemId = x.Ventures.First().ItemId,
                    })
                    .ToList();

            var charactersToCheck = _configuration.Characters
                .Where(x => x.Type != Configuration.CharacterType.NotManaged)
                .OrderBy(x => x.WorldName)
                .ThenBy(x => x.LocalContentId)
                .Select(x => new CheckedCharacter(_configuration, x, itemsToCheck))
                .ToList();

            if (checkPerCharacter)
            {
                foreach (var ch in charactersToCheck.Where(x => x.ToCheck(onlyShowMissing).Count != 0))
                {
                    bool currentCharacter = _clientState.LocalContentId == ch.Character.LocalContentId;
                    ImGui.BeginDisabled(currentCharacter);
                    if (ImGuiComponents.IconButton($"SwitchCharacters{ch.Character.LocalContentId}",
                            FontAwesomeIcon.DoorOpen))
                    {
                        _commandManager.ProcessCommand(
                            $"/ays relog {ch.Character.CharacterName}@{ch.Character.WorldName}");
                    }

                    ImGui.EndDisabled();
                    ImGui.SameLine();

                    if (currentCharacter)
                        ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.HealerGreen);

                    bool expanded = ImGui.CollapsingHeader($"{ch.Character}###GatheredCh{ch.Character.LocalContentId}");
                    if (currentCharacter)
                        ImGui.PopStyleColor();

                    if (expanded)
                    {
                        ImGui.Indent(_mainIndentSize + ImGui.GetStyle().FramePadding.X);
                        foreach (var item in itemsToCheck.Where(x =>
                                     ch.ToCheck(onlyShowMissing).ContainsKey(x.ItemId)))
                        {
                            var color = ch.Items[item.ItemId];
                            if (color != ColorGrey)
                            {
                                ImGui.PushStyleColor(ImGuiCol.Text, color);
                                if (currentCharacter && color == ColorRed)
                                {
                                    ImGui.Selectable(item.GatheredItem.Name);
                                    if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                                    {
                                        uint classJob = _clientState.LocalPlayer!.ClassJob.Id;
                                        if (classJob == 16)
                                            _commandManager.ProcessCommand($"/gathermin {item.GatheredItem.Name}");
                                        else if (classJob == 17)
                                            _commandManager.ProcessCommand($"/gatherbtn {item.GatheredItem.Name}");
                                        else if (classJob == 18)
                                            _commandManager.ProcessCommand($"/gatherfish {item.GatheredItem.Name}");
                                        else
                                            _commandManager.ProcessCommand($"/gather {item.GatheredItem.Name}");
                                    }
                                }
                                else
                                {
                                    ImGui.Text(item.GatheredItem.Name);
                                }

                                ImGui.PopStyleColor();
                            }
                        }

                        ImGui.Unindent(_mainIndentSize + ImGui.GetStyle().FramePadding.X);
                    }
                }
            }
            else
            {
                foreach (var item in itemsToCheck.Where(x =>
                             charactersToCheck.Any(y => y.ToCheck(onlyShowMissing).ContainsKey(x.ItemId))))
                {
                    if (ImGui.CollapsingHeader($"{item.GatheredItem.Name}##Gathered{item.GatheredItem.ItemId}"))
                    {
                        ImGui.Indent(_mainIndentSize + ImGui.GetStyle().FramePadding.X);
                        foreach (var ch in charactersToCheck)
                        {
                            var color = ch.Items[item.ItemId];
                            if (color == ColorRed || (color == ColorGreen && !onlyShowMissing))
                            {
                                bool currentCharacter = _clientState.LocalContentId == ch.Character.LocalContentId;
                                if (currentCharacter)
                                {
                                    ImGui.PushFont(UiBuilder.IconFont);
                                    var pos = ImGui.GetCursorPos();
                                    ImGui.SetCursorPos(pos with
                                    {
                                        X = pos.X - ImGui.CalcTextSize(CurrentCharPrefix).X - 5
                                    });
                                    ImGui.TextUnformatted(CurrentCharPrefix);
                                    ImGui.SetCursorPos(pos);
                                    ImGui.PopFont();
                                }

                                ImGui.PushStyleColor(ImGuiCol.Text, color);
                                ImGui.TextUnformatted(ch.Character.ToString());
                                ImGui.PopStyleColor();
                            }
                        }

                        ImGui.Unindent(_mainIndentSize + ImGui.GetStyle().FramePadding.X);
                    }
                }
            }

            ImGui.EndTabItem();
        }
    }

    private void DrawVentureListSelection(string id, List<Guid> selectedLists)
    {
        ImGui.PushID($"##ListSelection{id}");

        List<(Guid Id, string Name, Configuration.ItemList List)> itemLists = new List<Configuration.ItemList>
            {
                new()
                {
                    Id = Guid.Empty,
                    Name = "---",
                    Type = Configuration.ListType.CollectOneTime,
                    Priority = Configuration.ListPriority.InOrder,
                    CheckRetainerInventory = false,
                }
            }.Concat(_configuration.ItemLists)
            .Select(x => (x.Id, $"{x.Name} {x.GetIcon()}".TrimEnd(), x)).ToList();
        int? itemToRemove = null;
        int? itemToAdd = null;
        int indexToAdd = 0;
        for (int i = 0; i < selectedLists.Count; ++i)
        {
            ImGui.PushID($"##{id}_Item{i}");
            var listId = selectedLists[i];
            var listIndex = itemLists.FindIndex(x => x.Id == listId);

            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X -
                                                  ImGui.CalcTextSize(FontAwesomeIcon.ArrowUp.ToIconString()).X -
                                                  ImGui.CalcTextSize(FontAwesomeIcon.ArrowDown.ToIconString()).X -
                                                  ImGui.CalcTextSize(FontAwesomeIcon.Times.ToIconString()).X -
                                                  ImGui.GetStyle().FramePadding.X * 6 -
                                                  ImGui.GetStyle().ItemSpacing.X * 2);
            ImGui.PopFont();
            if (ImGui.Combo("", ref listIndex, itemLists.Select(x => x.Name).ToArray(), itemLists.Count))
            {
                selectedLists[i] = itemLists[listIndex].Id;
                Save();
            }

            if (selectedLists.Count > 1)
            {
                bool wrap = _configuration.ConfigUiOptions.WrapAroundWhenReordering;

                ImGui.SameLine();
                ImGui.BeginDisabled(i == 0 && !wrap);
                if (ImGuiComponents.IconButton($"##Up{i}", FontAwesomeIcon.ArrowUp))
                {
                    itemToAdd = i;
                    if (i > 0)
                        indexToAdd = i - 1;
                    else
                        indexToAdd = selectedLists.Count - 1;
                }

                ImGui.EndDisabled();

                ImGui.SameLine(0, 0);
                ImGui.BeginDisabled(i == selectedLists.Count - 1 && !wrap);
                if (ImGuiComponents.IconButton($"##Down{i}", FontAwesomeIcon.ArrowDown))
                {
                    itemToAdd = i;
                    if (i < selectedLists.Count - 1)
                        indexToAdd = i + 1;
                    else
                        indexToAdd = 0;
                }

                ImGui.EndDisabled();
                ImGui.SameLine();
            }
            else
            {
                ImGui.SameLine(0, 58);
            }

            if (ImGuiComponents.IconButton($"##Remove{i}", FontAwesomeIcon.Times))
                itemToRemove = i;

            if (listIndex > 0)
            {
                if (selectedLists.Take(i).Any(x => x == listId))
                {
                    ImGui.Indent(_mainIndentSize);
                    ImGui.TextColored(ImGuiColors.DalamudYellow, "This entry is a duplicate and will be ignored.");
                    ImGui.Unindent(_mainIndentSize);
                }
                else if (_configuration.ConfigUiOptions.ShowVentureListContents)
                {
                    var list = itemLists[listIndex].List;
                    ImGui.Indent(_mainIndentSize);
                    ImGui.Text(list.Type == Configuration.ListType.CollectOneTime
                        ? "Items on this list will be collected once."
                        : "Items on this list will be kept in stock on each character.");
                    ImGui.Spacing();
                    foreach (var item in list.Items)
                    {
                        var venture = _gameCache.Ventures.First(x => x.ItemId == item.ItemId);
                        ImGui.Text($"{item.RemainingQuantity}x {venture.Name}");
                    }

                    ImGui.Unindent(_mainIndentSize);
                }
            }

            ImGui.PopID();
        }

        if (itemToRemove != null)
        {
            selectedLists.RemoveAt(itemToRemove.Value);
            Save();
        }

        if (itemToAdd != null)
        {
            Guid listId = selectedLists[itemToAdd.Value];
            selectedLists.RemoveAt(itemToAdd.Value);
            selectedLists.Insert(indexToAdd, listId);
            Save();
        }

        var unusedLists = itemLists.Where(x => x.Id != Guid.Empty && !selectedLists.Contains(x.Id)).ToList();
        ImGui.BeginDisabled(unusedLists.Count == 0);
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "Add Venture List to this Group"))
            ImGui.OpenPopup($"##AddItem{id}");

        if (ImGui.BeginPopupContextItem($"##AddItem{id}", ImGuiPopupFlags.NoOpenOverItems))
        {
            foreach (var list in unusedLists)
            {
                if (ImGui.MenuItem($"{list.Name}##{list.Id}"))
                {
                    selectedLists.Add(list.Id);
                    Save();
                }
            }

            ImGui.EndPopup();
        }

        ImGui.EndDisabled();

        ImGui.PopID();
    }

    private void DrawMiscTab()
    {
        if (ImGui.BeginTabItem("Misc###TabMisc"))
        {
            ImGui.Text("Venture Settings");
            ImGui.Spacing();

            ImGui.SetNextItemWidth(130);
            int venturesToKeep = _configuration.Misc.VenturesToKeep;
            if (ImGui.InputInt("Minimum Ventures needed to assign retainers", ref venturesToKeep))
            {
                _configuration.Misc.VenturesToKeep = Math.Max(0, Math.Min(65000, venturesToKeep));
                Save();
            }

            ImGui.SameLine();
            ImGuiComponents.HelpMarker(
                $"If you have less than {venturesToKeep} ventures, retainers will only be sent out for Quick Ventures (instead of picking the next item from the Venture List).");

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.Text("User Interface Settings");

            bool showContents = _configuration.ConfigUiOptions.ShowVentureListContents;
            if (ImGui.Checkbox("Show Venture List preview in Groups/Retainer tabs", ref showContents))
            {
                _configuration.ConfigUiOptions.ShowVentureListContents = showContents;
                Save();
            }

            bool wrapAroundWhenReordering = _configuration.ConfigUiOptions.WrapAroundWhenReordering;
            if (ImGui.Checkbox("Allow sorting with up/down arrows to wrap around", ref wrapAroundWhenReordering))
            {
                _configuration.ConfigUiOptions.WrapAroundWhenReordering = wrapAroundWhenReordering;
                Save();
            }

            ImGuiComponents.HelpMarker(
                "When enabled:\n- Clicking the Up-Arrow for the first item in a list, that item will be moved to the bottom.\n- Clicking the Down-Arrow for the last item in the list, that item will be moved to the top.");

            ImGui.EndTabItem();
        }
    }

    private void Save()
    {
        _pluginInterface.SavePluginConfig(_configuration);
    }

    /// <summary>
    /// The default implementation for <see cref="ImGui.GetClipboardText"/> throws an NullReferenceException if the clipboard is empty, maybe also if it doesn't contain text.
    /// </summary>
    private unsafe string? GetClipboardText()
    {
        byte* ptr = ImGuiNative.igGetClipboardText();
        if (ptr == null)
            return null;

        int byteCount = 0;
        while (ptr[byteCount] != 0)
            ++byteCount;
        return Encoding.UTF8.GetString(ptr, byteCount);
    }

    private sealed class CheckedCharacter
    {
        public CheckedCharacter(Configuration configuration, Configuration.CharacterConfiguration character,
            List<CheckedItem> itemsToCheck)
        {
            Character = character;

            List<Guid> itemListIds = new();
            if (character.Type == Configuration.CharacterType.Standalone)
            {
                itemListIds = character.ItemListIds;
            }
            else if (character.Type == Configuration.CharacterType.PartOfCharacterGroup)
            {
                var group = configuration.CharacterGroups.SingleOrDefault(x => x.Id == character.CharacterGroupId);
                if (group != null)
                    itemListIds = group.ItemListIds;
            }

            var itemIdsOnLists = itemListIds.Where(listId => listId != Guid.Empty)
                .Select(listId => configuration.ItemLists.SingleOrDefault(x => x.Id == listId))
                .Where(list => list != null)
                .SelectMany(list => list!.Items)
                .Select(x => x.ItemId)
                .ToList();

            foreach (var item in itemsToCheck)
            {
                // check if the item is on any relevant list
                if (!itemIdsOnLists.Contains(item.ItemId))
                {
                    Items[item.ItemId] = ColorGrey;
                    continue;
                }

                // check if we are the correct job
                bool enabled = character.Retainers.Any(x => item.Ventures.Any(v => v.MatchesJob(x.Job)));
                if (enabled)
                {
                    // do we have it gathered on this char?
                    if (character.GatheredItems.Contains(item.GatheredItem.GatheredItemId))
                        Items[item.ItemId] = ColorGreen;
                    else
                        Items[item.ItemId] = ColorRed;
                }
                else
                    Items[item.ItemId] = ColorGrey;
            }
        }

        public Configuration.CharacterConfiguration Character { get; }
        public Dictionary<uint, Vector4> Items { get; } = new();

        public Dictionary<uint, Vector4> ToCheck(bool onlyShowMissing)
        {
            return Items
                .Where(x => x.Value == ColorRed || (x.Value == ColorGreen && !onlyShowMissing))
                .ToDictionary(x => x.Key, x => x.Value);
        }
    }

    private sealed class CheckedItem
    {
        public required ItemToGather GatheredItem { get; init; }
        public required List<Venture> Ventures { get; init; }
        public required uint ItemId { get; init; }
    }

    private sealed class TemporaryConfig
    {
        public required string Name { get; set; }
        public Configuration.ListType ListType { get; set; }
        public Configuration.ListPriority ListPriority { get; set; }
        public bool CheckRetainerInventory { get; set; }
    }
}
