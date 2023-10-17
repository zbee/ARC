using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ARControl.GameData;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.ImGuiMethods;
using ImGuiNET;
using LLib;

namespace ARControl.Windows;

internal sealed class ConfigWindow : Window
{
    // TODO This should also allow retainers under max level
    private const byte MinLevel = 10;

    private static readonly Vector4 ColorGreen = ImGuiColors.HealerGreen;
    private static readonly Vector4 ColorRed = ImGuiColors.DalamudRed;
    private static readonly Vector4 ColorGrey = ImGuiColors.DalamudGrey;
    private static readonly string[] StockingTypeLabels = { "Collect Once", "Keep in Stock" };

    private static readonly string[] PriorityLabels =
        { "Collect in order of the list", "Collect item with lowest inventory first" };

    private readonly DalamudPluginInterface _pluginInterface;
    private readonly Configuration _configuration;
    private readonly GameCache _gameCache;
    private readonly IClientState _clientState;
    private readonly ICommandManager _commandManager;
    private readonly IconCache _iconCache;
    private readonly IPluginLog _pluginLog;

    private readonly Dictionary<Guid, TemporaryConfig> _currentEditPopups = new();
    private string _searchString = string.Empty;
    private TemporaryConfig _newGroup = new() { Name = string.Empty };

    private TemporaryConfig _newList = new()
    {
        Name = string.Empty,
        ListType = Configuration.ListType.CollectOneTime,
        ListPriority = Configuration.ListPriority.InOrder
    };

    private bool _checkPerCharacter = true;
    private bool _onlyShowMissing = true;

    public ConfigWindow(
        DalamudPluginInterface pluginInterface,
        Configuration configuration,
        GameCache gameCache,
        IClientState clientState,
        ICommandManager commandManager,
        IconCache iconCache,
        IPluginLog pluginLog)
        : base($"ARC {SeIconChar.Collectible.ToIconString()}###ARControlConfig")
    {
        _pluginInterface = pluginInterface;
        _configuration = configuration;
        _gameCache = gameCache;
        _clientState = clientState;
        _commandManager = commandManager;
        _iconCache = iconCache;
        _pluginLog = pluginLog;

        SizeConstraints = new()
        {
            MinimumSize = new Vector2(480, 300),
            MaximumSize = new Vector2(9999, 9999),
        };
    }

    public override void Draw()
    {
        LImGui.AddPatreonIcon(_pluginInterface);

        if (ImGui.BeginTabBar("ARConfigTabs"))
        {
            DrawVentureLists();
            DrawCharacterGroups();
            DrawCharacters();
            DrawGatheredItemsToCheck();
            ImGui.EndTabBar();
        }
    }

    private void DrawVentureLists()
    {
        if (ImGui.BeginTabItem("Venture Lists"))
        {
            Configuration.ItemList? listToDelete = null;
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
                    };
                    ImGui.OpenPopup($"##EditList{list.Id}");
                }

                DrawVentureListEditorPopup(list, ref listToDelete);

                ImGui.SameLine();

                string label = $"{list.Name} {list.GetIcon()}";

                if (ImGui.CollapsingHeader(label))
                {
                    ImGui.Indent(30);
                    DrawVentureListItemSelection(list);
                    ImGui.Unindent(30);
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
                                             list.Priority == temporaryConfig.ListPriority));
            save |= ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Save, "Save");
            ImGui.EndDisabled();

            if (save && canSave)
            {
                list.Name = temporaryConfig.Name;
                list.Type = temporaryConfig.ListType;

                if (list.Type == Configuration.ListType.CollectOneTime)
                    list.Priority = Configuration.ListPriority.InOrder;
                else
                    list.Priority = temporaryConfig.ListPriority;

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
                });

                _newList = new()
                {
                    Name = string.Empty,
                    ListType = Configuration.ListType.CollectOneTime,
                    ListPriority = Configuration.ListPriority.InOrder
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
        string listName = temporaryConfig.Name;
        bool save = ImGui.InputTextWithHint("", "List Name...", ref listName, 64,
            ImGuiInputTextFlags.EnterReturnsTrue);
        bool canSave = IsValidListName(listName, list);
        temporaryConfig.Name = listName;

        ImGui.PushID($"Type{list?.Id ?? Guid.Empty}");
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
            int priority = (int)temporaryConfig.ListPriority;
            if (ImGui.Combo("", ref priority, PriorityLabels, PriorityLabels.Length))
                temporaryConfig.ListPriority = (Configuration.ListPriority)priority;
            ImGui.PopID();
        }

        return (save, canSave);
    }

    private void DrawVentureListItemSelection(Configuration.ItemList list)
    {
        ImGuiEx.SetNextItemFullWidth();
        if (ImGui.BeginCombo($"##VentureSelection{list.Id}", "Add Venture...", ImGuiComboFlags.HeightLarge))
        {
            ImGuiEx.SetNextItemFullWidth();
            ImGui.InputTextWithHint("", "Filter...", ref _searchString, 256, ImGuiInputTextFlags.AutoSelectAll);

            foreach (var ventures in _gameCache.Ventures
                         .Where(x => x.Name.ToLower().Contains(_searchString.ToLower()))
                         .OrderBy(x => x.Level)
                         .ThenBy(x => x.Name)
                         .ThenBy(x => x.ItemId)
                         .GroupBy(x => x.ItemId))
            {
                var venture = ventures.First();
                IDalamudTextureWrap? icon = _iconCache.GetIcon(venture.IconId);
                if (icon != null)
                {
                    ImGui.Image(icon.ImGuiHandle, new Vector2(23, 23));
                    ImGui.SameLine();
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3);
                }

                if (ImGui.Selectable(
                        $"{venture.Name} ({string.Join(" ", ventures.Select(x => x.CategoryName))})##SelectVenture{venture.RowId}"))
                {
                    list.Items.Add(new Configuration.QueuedItem
                    {
                        ItemId = venture.ItemId,
                        RemainingQuantity = 0,
                    });
                    Save();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.Spacing();

        Configuration.QueuedItem? itemToRemove = null;
        Configuration.QueuedItem? itemToAdd = null;
        int indexToAdd = 0;
        float windowX = ImGui.GetContentRegionAvail().X;
        for (int i = 0; i < list.Items.Count; ++i)
        {
            var item = list.Items[i];
            ImGui.PushID($"QueueItem{i}");
            var ventures = _gameCache.Ventures.Where(x => x.ItemId == item.ItemId).ToList();
            var venture = ventures.First();

            IDalamudTextureWrap? icon = _iconCache.GetIcon(venture.IconId);
            if (icon != null)
            {
                ImGui.Image(icon.ImGuiHandle, new Vector2(23, 23));
                ImGui.SameLine(0, 3);
            }

            ImGui.SetNextItemWidth(130);
            int quantity = item.RemainingQuantity;
            if (ImGui.InputInt($"{venture.Name} ({string.Join(" ", ventures.Select(x => x.CategoryName))})",
                    ref quantity, 100))
            {
                item.RemainingQuantity = quantity;
                Save();
            }

            ImGui.SameLine(windowX - 30);
            ImGui.BeginDisabled(i == 0);
            if (ImGuiComponents.IconButton($"##Up{i}", FontAwesomeIcon.ArrowUp))
            {
                itemToAdd = item;
                indexToAdd = i - 1;
            }

            ImGui.EndDisabled();

            ImGui.SameLine(0, 0);
            ImGui.BeginDisabled(i == list.Items.Count - 1);
            if (ImGuiComponents.IconButton($"##Down{i}", FontAwesomeIcon.ArrowDown))
            {
                itemToAdd = item;
                indexToAdd = i + 1;
            }

            ImGui.EndDisabled();

            ImGui.SameLine();
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

        if (list.Items.Count > 0 && list.Type == Configuration.ListType.CollectOneTime)
        {
            ImGui.Spacing();
            ImGui.BeginDisabled(list.Items.All(x => x.RemainingQuantity > 0));
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Check, "Remove all finished items"))
            {
                list.Items.RemoveAll(q => q.RemainingQuantity <= 0);
                Save();
            }

            ImGui.EndDisabled();
        }

        ImGui.Spacing();
    }

    private void DrawCharacters()
    {
        if (ImGui.BeginTabItem("Retainers"))
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
                    Vector4 buttonColor = new Vector4();
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
                        ImGui.Indent(30);

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
                                    // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
                                    if (character.ItemListIds == null)
                                        character.ItemListIds = new();
                                    DrawVentureListSelection(character.LocalContentId.ToString(),
                                        character.ItemListIds);
                                }
                                else
                                {
                                    ImGui.TextWrapped($"Retainers will participate in the following lists:");
                                    ImGui.Indent(30);

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

                                    ImGui.Unindent(30);
                                    ImGui.Spacing();
                                }

                                ImGui.EndTabItem();
                            }

                            if (ImGui.BeginTabItem("Retainers"))
                            {
                                foreach (var retainer in character.Retainers.Where(x => x.Job > 0)
                                             .OrderBy(x => x.DisplayOrder))
                                {
                                    ImGui.BeginDisabled(retainer.Level < MinLevel);

                                    bool managed = retainer.Managed && retainer.Level >= MinLevel;

                                    IDalamudTextureWrap? icon = _iconCache.GetIcon(62000 + retainer.Job);
                                    if (icon != null)
                                    {
                                        ImGui.Image(icon.ImGuiHandle, new Vector2(23, 23));
                                        ImGui.SameLine();
                                    }

                                    if (ImGui.Checkbox(
                                            $"{retainer.Name}###Retainer{retainer.Name}{retainer.DisplayOrder}",
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


                        ImGui.Unindent(30);
                    }

                    ImGui.PopID();
                }
            }

            ImGui.EndTabItem();
        }
    }

    private void DrawCharacterGroups()
    {
        if (ImGui.BeginTabItem("Groups"))
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
            ImGui.Indent(30);
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
                    ImGui.Indent(30);
                    foreach (var character in assignedCharacters.OrderBy(x => x.WorldName)
                                 .ThenBy(x => x.LocalContentId))
                        ImGui.TextUnformatted($"{character.CharacterName} @ {character.WorldName}");
                    ImGui.Unindent(30);
                }

                ImGui.EndTabBar();
            }

            ImGui.Unindent(30);
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
               !name.Contains('%') &&
               !_configuration.CharacterGroups.Any(x => x != existingGroup && name.EqualsIgnoreCase(x.Name));
    }

    private bool IsValidListName(string name, Configuration.ItemList? existingList)
    {
        return name.Length >= 2 &&
               !name.Contains('%') &&
               !_configuration.ItemLists.Any(x => x != existingList && name.EqualsIgnoreCase(x.Name));
    }

    private void DrawGatheredItemsToCheck()
    {
        if (ImGui.BeginTabItem("Locked Items"))
        {
            ImGui.Checkbox("Group by character", ref _checkPerCharacter);
            ImGui.Checkbox("Only show missing items", ref _onlyShowMissing);
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

            if (_checkPerCharacter)
            {
                foreach (var ch in charactersToCheck.Where(x => x.ToCheck(_onlyShowMissing).Any()))
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
                        ImGui.Indent(30);
                        foreach (var item in itemsToCheck.Where(x =>
                                     ch.ToCheck(_onlyShowMissing).ContainsKey(x.ItemId)))
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

                        ImGui.Unindent(30);
                    }
                }
            }
            else
            {
                foreach (var item in itemsToCheck.Where(x =>
                             charactersToCheck.Any(y => y.ToCheck(_onlyShowMissing).ContainsKey(x.ItemId))))
                {
                    if (ImGui.CollapsingHeader($"{item.GatheredItem.Name}##Gathered{item.GatheredItem.ItemId}"))
                    {
                        ImGui.Indent(30);
                        foreach (var ch in charactersToCheck)
                        {
                            var color = ch.Items[item.ItemId];
                            if (color == ColorRed || (color == ColorGreen && !_onlyShowMissing))
                                ImGui.TextColored(color, ch.Character.ToString());
                        }

                        ImGui.Unindent(30);
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
                new Configuration.ItemList
                {
                    Id = Guid.Empty,
                    Name = "---",
                    Type = Configuration.ListType.CollectOneTime,
                    Priority = Configuration.ListPriority.InOrder,
                }
            }.Concat(_configuration.ItemLists)
            .Select(x => (x.Id, x.Name, x)).ToList();
        int? itemToRemove = null;
        int? itemToAdd = null;
        int indexToAdd = 0;
        float windowX = ImGui.GetContentRegionAvail().X;
        for (int i = 0; i < selectedLists.Count; ++i)
        {
            ImGui.PushID($"##{id}_Item{i}");
            var listId = selectedLists[i];
            var listIndex = itemLists.FindIndex(x => x.Id == listId);

            ImGui.SetNextItemWidth(windowX - 76);
            if (ImGui.Combo("", ref listIndex, itemLists.Select(x => x.Name).ToArray(), itemLists.Count))
            {
                selectedLists[i] = itemLists[listIndex].Id;
                Save();
            }

            ImGui.SameLine();
            ImGui.BeginDisabled(i == 0);
            if (ImGuiComponents.IconButton($"##Up{i}", FontAwesomeIcon.ArrowUp))
            {
                itemToAdd = i;
                indexToAdd = i - 1;
            }

            ImGui.EndDisabled();

            ImGui.SameLine(0, 0);
            ImGui.BeginDisabled(i == selectedLists.Count - 1);
            if (ImGuiComponents.IconButton($"##Down{i}", FontAwesomeIcon.ArrowDown))
            {
                itemToAdd = i;
                indexToAdd = i + 1;
            }

            ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGuiComponents.IconButton($"##Remove{i}", FontAwesomeIcon.Times))
                itemToRemove = i;

            if (listIndex > 0)
            {
                if (selectedLists.Take(i).Any(x => x == listId))
                {
                    ImGui.Indent(30);
                    ImGui.TextColored(ImGuiColors.DalamudYellow, "This entry is a duplicate and will be ignored.");
                    ImGui.Unindent(30);
                }
                else
                {
                    var list = itemLists[listIndex].List;
                    ImGui.Indent(30);
                    ImGui.Text(list.Type == Configuration.ListType.CollectOneTime
                        ? $"{list.GetIcon()} Items on this list will be collected once."
                        : $"{list.GetIcon()} Items on this list will be kept in stock on each character.");
                    ImGui.Spacing();
                    foreach (var item in list.Items)
                    {
                        var venture = _gameCache.Ventures.First(x => x.ItemId == item.ItemId);
                        ImGui.Text($"{item.RemainingQuantity}x {venture.Name}");
                    }

                    ImGui.Unindent(30);
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

    private void Save()
    {
        _pluginInterface.SavePluginConfig(_configuration);
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
    }
}
