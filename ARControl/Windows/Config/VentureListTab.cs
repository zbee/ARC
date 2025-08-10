using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using ARControl.External;
using ARControl.GameData;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.ImGuiMethods;
using LLib;

namespace ARControl.Windows.Config;

internal sealed class VentureListTab : ITab
{
    private static readonly string[] StockingTypeLabels = ["Collect Once", "Keep in Stock"];

    private static readonly string[] PriorityLabels =
        { "Collect in order of the list", "Collect item with lowest inventory first" };

    private static readonly Regex CountAndName = new(@"^(\d{1,5})x?\s+(.*)$", RegexOptions.Compiled);
    private const FontAwesomeIcon WarningIcon = FontAwesomeIcon.ExclamationCircle;
    private const FontAwesomeIcon ExcessCrystalsIcon = FontAwesomeIcon.Diamond;

    private readonly ConfigWindow _configWindow;
    private readonly Configuration _configuration;
    private readonly GameCache _gameCache;
    private readonly ITextureProvider _textureProvider;
    private readonly DiscardHelperIpc _discardHelperIpc;
    private readonly IPluginLog _pluginLog;

    private string _searchString = string.Empty;
    private (Guid, Guid)? _draggedItem;

    private readonly Dictionary<Guid, TemporaryConfig> _currentEditPopups = new();

    private TemporaryConfig _newList = new()
    {
        Name = string.Empty,
        ListType = Configuration.ListType.CollectOneTime,
        ListPriority = Configuration.ListPriority.InOrder,
        CheckRetainerInventory = false,
    };

    public VentureListTab(ConfigWindow configWindow, Configuration configuration, GameCache gameCache,
        ITextureProvider textureProvider, DiscardHelperIpc discardHelperIpc, IPluginLog pluginLog)
    {
        _configWindow = configWindow;
        _configuration = configuration;
        _gameCache = gameCache;
        _textureProvider = textureProvider;
        _discardHelperIpc = discardHelperIpc;
        _pluginLog = pluginLog;
    }

    public void Draw()
    {
        using var tab = ImRaii.TabItem("Venture Lists###TabVentureLists");
        if (!tab)
            return;

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
                ImGui.Indent(_configWindow.MainIndentSize);
                DrawVentureListItemSelection(list, itemsToDiscard);
                ImGui.Unindent(_configWindow.MainIndentSize);
            }

            ImGui.PopID();
        }

        if (listToDelete != null)
        {
            _configuration.ItemLists.Remove(listToDelete);
            _configWindow.ShouldSave();
        }

        ImGui.Separator();
        DrawNewVentureList();
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
                                             list.CheckRetainerInventory ==
                                             temporaryConfig.CheckRetainerInventory));
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
                _configWindow.ShouldSave();
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

    private void DrawVentureListItemSelection(Configuration.ItemList list, IReadOnlySet<uint> itemsToDiscard)
    {
        DrawVentureListItemFilter(list);

        Configuration.QueuedItem? itemToRemove = null;
        Configuration.QueuedItem? itemToAdd = null;
        int indexToAdd = 0;

        float width = ImGui.GetContentRegionAvail().X;
        List<(Vector2 TopLeft, Vector2 BottomRight)> itemPositions = [];

        for (int i = 0; i < list.Items.Count; ++i)
        {
            Vector2 topLeft = ImGui.GetCursorScreenPos() +
                              new Vector2(-_configWindow.MainIndentSize, -ImGui.GetStyle().ItemSpacing.Y / 2);

            var item = list.Items[i];
            ImGui.PushID($"QueueItem{item.InternalId}");
            var ventures = _gameCache.Ventures.Where(x => x.ItemId == item.ItemId).ToList();
            var venture = ventures.First();

            if (itemsToDiscard.Contains(venture.ItemId))
                DrawWarning(WarningIcon, "This item will be automatically discarded by 'Discard Helper'.");
            else if (item.ItemId is >= 2 and <= 13 && item.RemainingQuantity >= 10000)
            {
                if (list.Type == Configuration.ListType.CollectOneTime || list.CheckRetainerInventory)
                    DrawWarning(ExcessCrystalsIcon,
                        "You are responsible for manually moving shards or crystals to your retainers - ARC won't do that for you.\nIf you don't, this may lead to wasted ventures.",
                        ImGuiColors.ParsedBlue);
                else
                    DrawWarning(WarningIcon, "You can never have this many of a shard or crystal in your inventory.");
            }

            var icon = _textureProvider.GetFromGameIcon(new GameIconLookup(venture.IconId));
            ImGui.Image(icon.GetWrapOrEmpty().Handle, new Vector2(ImGui.GetFrameHeight()));
            ImGui.SameLine(0, ImGui.GetStyle().FramePadding.X);

            ImGui.SetNextItemWidth(130 * ImGuiHelpers.GlobalScale);
            int quantity = item.RemainingQuantity;
            if (ImGui.InputInt($"{venture.Name} ({string.Join(" ", ventures.Select(x => x.CategoryType.ToString()))})",
                    ref quantity, 100))
            {
                item.RemainingQuantity = quantity;
                _configWindow.ShouldSave();
            }

            DrawDragDropIcons(list, item);

            if (ImGuiComponents.IconButton($"##Remove{i}", FontAwesomeIcon.Times))
                itemToRemove = item;

            ImGui.PopID();

            Vector2 bottomRight = new Vector2(topLeft.X + width + _configWindow.MainIndentSize,
                ImGui.GetCursorScreenPos().Y - ImGui.GetStyle().ItemSpacing.Y + 2);
            //ImGui.GetWindowDrawList().AddRect(topLeft, bottomRight, ImGui.GetColorU32(i % 2 == 0 ? ImGuiColors.DalamudRed : ImGuiColors.HealerGreen));
            itemPositions.Add((topLeft, bottomRight));
        }

        if (!ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            _draggedItem = null;
        else if (_draggedItem != null && _draggedItem.Value.Item1 == list.Id)
        {
            var draggedItem = list.Items.Single(x => x.InternalId == _draggedItem.Value.Item2);
            int oldIndex = list.Items.IndexOf(draggedItem);

            var (topLeft, bottomRight) = itemPositions[oldIndex];
            if (!itemsToDiscard.Contains(draggedItem.ItemId))
                topLeft += new Vector2(_configWindow.MainIndentSize, 0);
            ImGui.GetWindowDrawList().AddRect(topLeft, bottomRight, ImGui.GetColorU32(ImGuiColors.DalamudGrey), 3f,
                ImDrawFlags.RoundCornersAll);

            int newIndex = itemPositions.FindIndex(x => ImGui.IsMouseHoveringRect(x.TopLeft, x.BottomRight, true));
            if (newIndex >= 0 && oldIndex != newIndex)
            {
                itemToAdd = list.Items.Single(x => x.InternalId == _draggedItem.Value.Item2);
                indexToAdd = newIndex;
            }
        }

        if (itemToRemove != null)
        {
            list.Items.Remove(itemToRemove);
            _configWindow.ShouldSave();
        }

        if (itemToAdd != null)
        {
            _pluginLog.Information($"Updating {itemToAdd.ItemId} → {indexToAdd}");
            list.Items.Remove(itemToAdd);
            list.Items.Insert(indexToAdd, itemToAdd);
            _configWindow.ShouldSave();
        }

        ImGui.Spacing();
        List<Configuration.QueuedItem> clipboardItems = ParseClipboardItems();
        ImportFromClipboardButton(list, clipboardItems);
        RemoveFinishedItemsButton(list);

        ImGui.Spacing();
    }

    private void DrawDragDropIcons(Configuration.ItemList list, Configuration.QueuedItem item)
    {
        if (list.Items.Count > 1)
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.SameLine(ImGui.GetContentRegionAvail().X +
                           _configWindow.MainIndentSize +
                           ImGui.GetStyle().WindowPadding.X -
                           ImGui.CalcTextSize(FontAwesomeIcon.ArrowsUpDown.ToIconString()).X -
                           ImGui.CalcTextSize(FontAwesomeIcon.Times.ToIconString()).X -
                           ImGui.GetStyle().FramePadding.X * 4 -
                           ImGui.GetStyle().ItemSpacing.X);
            ImGui.PopFont();

            if (_draggedItem != null && _draggedItem.Value.Item1 == list.Id &&
                _draggedItem.Value.Item2 == item.InternalId)
            {
                ImGuiComponents.IconButton("##Move", FontAwesomeIcon.ArrowsUpDown,
                    ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.ButtonActive)));
            }
            else
                ImGuiComponents.IconButton("##Move", FontAwesomeIcon.ArrowsUpDown);

            if (_draggedItem == null && ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                _draggedItem = (list.Id, item.InternalId);

            ImGui.SameLine();
        }
        else
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.SameLine(ImGui.GetContentRegionAvail().X +
                           _configWindow.MainIndentSize +
                           ImGui.GetStyle().WindowPadding.X -
                           ImGui.CalcTextSize(FontAwesomeIcon.Times.ToIconString()).X -
                           ImGui.GetStyle().FramePadding.X * 2);
            ImGui.PopFont();
        }
    }

    private static void DrawWarning(FontAwesomeIcon icon, string tooltip, Vector4? color = null)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        var pos = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(pos.X - ImGui.CalcTextSize(icon.ToIconString()).X - 5, pos.Y + 2));
        ImGui.TextColored(color ?? ImGuiColors.DalamudYellow, icon.ToIconString());
        ImGui.SetCursorPos(pos);
        ImGui.PopFont();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
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
                _configWindow.ShouldSave();
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

    private void DrawVentureListItemFilter(Configuration.ItemList list)
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
                             CategoryNames = x.Select(y => y.CategoryType.ToString())
                         }))
            {
                var icon = _textureProvider.GetFromGameIcon(new GameIconLookup(filtered.Venture.IconId));
                Vector2 pos = ImGui.GetCursorPos();
                Vector2 iconSize = new Vector2(ImGui.GetTextLineHeight() + ImGui.GetStyle().ItemSpacing.Y);

                ImGui.SetCursorPos(pos + new Vector2(iconSize.X + ImGui.GetStyle().FramePadding.X,
                    ImGui.GetStyle().ItemSpacing.Y / 2));

                bool addThis = ImGui.Selectable(
                    $"{filtered.Venture.Name} ({string.Join(" ", filtered.CategoryNames)})##SelectVenture{filtered.Venture.RowId}");


                ImGui.SameLine(0, 0);
                ImGui.SetCursorPos(pos);
                ImGui.Image(icon.GetWrapOrEmpty().Handle, iconSize);

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

                    _configWindow.ShouldSave();
                }
            }

            ImGui.EndCombo();
        }

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

            _configWindow.ShouldSave();
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
                _configWindow.ShouldSave();
            }

            ImGui.EndDisabled();
        }
    }

    private List<Configuration.QueuedItem> ParseClipboardItems()
    {
        List<Configuration.QueuedItem> clipboardItems = new List<Configuration.QueuedItem>();
        try
        {
            string clipboardText = ImGui.GetClipboardText();
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

    private bool IsValidListName(string name, Configuration.ItemList? existingList)
    {
        return name.Length >= 2 &&
               !name.Contains('%', StringComparison.Ordinal) &&
               !_configuration.ItemLists.Any(x => x != existingList && name.EqualsIgnoreCase(x.Name));
    }

    private sealed class TemporaryConfig
    {
        public required string Name { get; set; }
        public Configuration.ListType ListType { get; set; }
        public Configuration.ListPriority ListPriority { get; set; }
        public bool CheckRetainerInventory { get; set; }
    }
}
