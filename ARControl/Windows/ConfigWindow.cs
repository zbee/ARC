using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ARControl.GameData;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.ImGuiMethods;
using ImGuiNET;

namespace ARControl.Windows;

internal sealed class ConfigWindow : Window
{
    private const byte MaxLevel = 90;

    private static readonly Vector4 ColorGreen = ImGuiColors.HealerGreen;
    private static readonly Vector4 ColorRed = ImGuiColors.DalamudRed;
    private static readonly Vector4 ColorGrey = ImGuiColors.DalamudGrey;

    private readonly DalamudPluginInterface _pluginInterface;
    private readonly Configuration _configuration;
    private readonly GameCache _gameCache;
    private readonly IClientState _clientState;
    private readonly ICommandManager _commandManager;
    private readonly IPluginLog _pluginLog;

    private string _searchString = string.Empty;
    private Configuration.QueuedItem? _dragDropSource;
    private bool _enableDragDrop;
    private string _newGroupName = string.Empty;
    private bool _checkPerCharacter = true;
    private bool _onlyShowMissing = true;

    public ConfigWindow(
        DalamudPluginInterface pluginInterface,
        Configuration configuration,
        GameCache gameCache,
        IClientState clientState,
        ICommandManager commandManager,
        IPluginLog pluginLog)
        : base("ARC###ARControlConfig")
    {
        _pluginInterface = pluginInterface;
        _configuration = configuration;
        _gameCache = gameCache;
        _clientState = clientState;
        _commandManager = commandManager;
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
            //DrawItemQueue();
            DrawCharacters();
            DrawCharacterGroups();
            //DrawGatheredItemsToCheck();
            ImGui.EndTabBar();
        }
    }

    /*
    private unsafe void DrawItemQueue()
    {
        if (ImGui.BeginTabItem("Item Lists"))
        {
            if (ImGui.BeginCombo("Add Item...##VentureSelection", ""))
            {
                ImGuiEx.SetNextItemFullWidth();
                ImGui.InputTextWithHint("", "Filter...", ref _searchString, 256);

                foreach (var ventures in _gameCache.Ventures
                             .Where(x => x.Name.ToLower().Contains(_searchString.ToLower()))
                             .OrderBy(x => x.Level)
                             .ThenBy(x => x.Name)
                             .ThenBy(x => x.ItemId)
                             .GroupBy(x => x.ItemId))
                {
                    var venture = ventures.First();
                    if (ImGui.Selectable(
                            $"{venture.Name} ({string.Join(" ", ventures.Select(x => x.CategoryName))})##SelectVenture{venture.RowId}"))
                    {
                        _configuration.QueuedItems.Add(new Configuration.QueuedItem
                        {
                            ItemId = venture.ItemId,
                            RemainingQuantity = 0,
                        });
                        _searchString = string.Empty;
                        Save();
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.Separator();

            ImGui.Indent(30);

            Configuration.QueuedItem? itemToRemove = null;
            Configuration.QueuedItem? itemToAdd = null;
            int indexToAdd = 0;
            for (int i = 0; i < _configuration.QueuedItems.Count; ++i)
            {
                var item = _configuration.QueuedItems[i];
                ImGui.PushID($"QueueItem{i}");
                var ventures = _gameCache.Ventures.Where(x => x.ItemId == item.ItemId).ToList();
                var venture = ventures.First();

                if (!_enableDragDrop)
                {
                    ImGui.SetNextItemWidth(130);
                    int quantity = item.RemainingQuantity;
                    if (ImGui.InputInt($"{venture.Name} ({string.Join(" ", ventures.Select(x => x.CategoryName))})",
                            ref quantity, 100))
                    {
                        item.RemainingQuantity = quantity;
                        Save();
                    }
                }
                else
                {
                    ImGui.Selectable($"{item.RemainingQuantity}x {venture.Name}");

                    if (ImGui.BeginDragDropSource())
                    {
                        ImGui.SetDragDropPayload("ArcDragDrop", nint.Zero, 0);
                        _dragDropSource = item;

                        ImGui.EndDragDropSource();
                    }

                    if (ImGui.BeginDragDropTarget())
                    {
                        if (_dragDropSource != null && ImGui.AcceptDragDropPayload("ArcDragDrop").NativePtr != null)
                        {
                            itemToAdd = _dragDropSource;
                            indexToAdd = i;

                            _dragDropSource = null;
                        }

                        ImGui.EndDragDropTarget();
                    }
                }

                ImGui.OpenPopupOnItemClick($"###ctx{i}", ImGuiPopupFlags.MouseButtonRight);
                if (ImGui.BeginPopupContextItem($"###ctx{i}"))
                {
                    if (ImGui.MenuItem($"Remove {venture.Name}"))
                        itemToRemove = item;

                    ImGui.EndPopup();
                }

                ImGui.PopID();
            }

            if (itemToRemove != null)
            {
                _configuration.QueuedItems.Remove(itemToRemove);
                Save();
            }

            if (itemToAdd != null)
            {
                _pluginLog.Information($"Updating {itemToAdd.ItemId} → {indexToAdd}");
                _configuration.QueuedItems.Remove(itemToAdd);
                _configuration.QueuedItems.Insert(indexToAdd, itemToAdd);
                Save();
            }

            ImGui.Unindent(30);

            if (_configuration.QueuedItems.Count > 0)
                ImGui.Separator();

            if (ImGuiComponents.IconButtonWithText(_enableDragDrop ? FontAwesomeIcon.Times : FontAwesomeIcon.Sort, _enableDragDrop ? "Disable Drag&Drop" : "Enable Drag&Drop"))
            {
                _enableDragDrop = !_enableDragDrop;
            }

            ImGui.SameLine();
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Check, "Remove all finished items"))
            {
                if (_configuration.QueuedItems.RemoveAll(q => q.RemainingQuantity == 0) > 0)
                    Save();
            }

            ImGui.EndTabItem();
        }
    }
*/
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
                            if (character.Type != Configuration.CharacterType.NotManaged && ImGui.BeginTabItem("Venture Lists"))
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
                                    DrawListSelection(character.LocalContentId.ToString(), character.ItemListIds);
                                }
                                else
                                {
                                    ImGui.TextWrapped($"Retainers will participate in the following lists:");
                                    ImGui.Indent(30);

                                    var group = _configuration.CharacterGroups.Single(x => x.Id == groups[groupIndex].Id);
                                    var lists = group.ItemListIds
                                        .Where(listId => listId != Guid.Empty)
                                        .Select(listId => _configuration.ItemLists.SingleOrDefault(x => x.Id == listId))
                                        .ToList();
                                    if (lists.Count > 0)
                                    {
                                        foreach (var list in lists)
                                            ImGui.TextUnformatted($"{SeIconChar.LinkMarker.ToIconChar()} {list.Name}");
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
                                foreach (var retainer in character.Retainers.Where(x => x.Job > 0).OrderBy(x => x.DisplayOrder))
                                {
                                    ImGui.BeginDisabled(retainer.Level < MaxLevel);

                                    bool managed = retainer.Managed && retainer.Level == MaxLevel;
                                    ImGui.Text(_gameCache.Jobs[retainer.Job]);
                                    ImGui.SameLine();
                                    if (ImGui.Checkbox($"{retainer.Name}###Retainer{retainer.Name}{retainer.DisplayOrder}",
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
            foreach (var group in _configuration.CharacterGroups)
            {
                ImGui.PushID($"##Group{group.Id}");

                ImGuiComponents.IconButton(FontAwesomeIcon.Cog);
                ImGui.SameLine();

                var assignedCharacters = _configuration.Characters
                    .Where(x => x.Type == Configuration.CharacterType.PartOfCharacterGroup &&
                                x.CharacterGroupId == group.Id)
                    .ToList();
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
                            DrawListSelection(group.Id.ToString(), group.ItemListIds);
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

                ImGui.PopID();
            }

            ImGui.Separator();

            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "Add Group"))
                ImGui.OpenPopup("##AddGroup");

            if (ImGui.BeginPopup("##AddGroup"))
            {
                bool save = ImGui.InputTextWithHint("", "Group Name...", ref _newGroupName, 64, ImGuiInputTextFlags.EnterReturnsTrue);
                bool canSave = _newGroupName.Length >= 2 &&
                               !_configuration.CharacterGroups.Any(x => _newGroupName.EqualsIgnoreCase(x.Name));
                ImGui.BeginDisabled(!canSave);
                save |= ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Save, "Save");
                ImGui.EndDisabled();

                if (canSave && save)
                {
                    _configuration.CharacterGroups.Add(new Configuration.CharacterGroup
                    {
                        Id = Guid.NewGuid(),
                        Name = _newGroupName,
                        Icon = FontAwesomeIcon.None,
                        ItemListIds = new(),
                    });

                    _newGroupName = string.Empty;

                    ImGui.CloseCurrentPopup();
                    Save();
                }
                ImGui.EndPopup();
            }

            ImGui.EndTabItem();
        }
    }

    /*
    private void DrawGatheredItemsToCheck()
    {
        if (ImGui.BeginTabItem("Locked Items"))
        {
            ImGui.Checkbox("Group by character", ref _checkPerCharacter);
            ImGui.Checkbox("Only show missing items", ref _onlyShowMissing);
            ImGui.Separator();

            var itemsToCheck =
                _configuration.QueuedItems
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
                .Where(x => x.Managed)
                .OrderBy(x => x.WorldName)
                .ThenBy(x => x.LocalContentId)
                .Select(x => new CheckedCharacter(x, itemsToCheck))
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
    }*/

    private void DrawListSelection(string id, List<Guid> selectedLists)
    {
        ImGui.PushID($"##ListSelection{id}");

        List<(Guid Id, string Name, Configuration.ItemList List)> itemLists = new List<Configuration.ItemList>
            {
                new Configuration.ItemList
                {
                    Id = Guid.Empty,
                    Name = "---",
                    Type = Configuration.ListType.CollectOneTime,
                }
            }.Concat(_configuration.ItemLists)
            .Select(x => (x.Id, x.Name, x)).ToList();
        int? itemToRemove = null;
        for (int i = 0; i < selectedLists.Count; ++i)
        {

            ImGui.PushID($"##{id}_Item{i}");
            var listId = selectedLists[i];
            var listIndex = itemLists.FindIndex(x => x.Id == listId);

            if (ImGui.Combo("", ref listIndex, itemLists.Select(x => x.Name).ToArray(), itemLists.Count))
            {
                selectedLists[i] = itemLists[listIndex].Id;
                Save();
            }

            ImGui.SameLine();
            if (ImGuiComponents.IconButton($"##Jump{i}", FontAwesomeIcon.Edit))
            {

            }

            ImGui.SameLine();
            if (ImGuiComponents.IconButton($"##Up{i}", FontAwesomeIcon.ArrowUp))
            {

            }

            ImGui.SameLine(0, 0);
            if (ImGuiComponents.IconButton($"##Down{i}", FontAwesomeIcon.ArrowDown))
            {

            }

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
                        ? $"{SeIconChar.LinkMarker.ToIconString()} Items on this list will be collected once."
                        : $"{SeIconChar.LinkMarker.ToIconString()} Items on this list will be kept in stock on each character.");
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

        var unusedLists = itemLists.Where(x => x.Id != Guid.Empty && !selectedLists.Contains(x.Id)).ToList();
        ImGui.BeginDisabled(unusedLists.Count == 0);
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "Add Venture List to this Group"))
            ImGui.OpenPopup($"##AddItem{id}");

        if (ImGui.BeginPopupContextItem($"##AddItem{id}"))
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
        public CheckedCharacter(Configuration.CharacterConfiguration character,
            List<CheckedItem> itemsToCheck)
        {
            Character = character;

            foreach (var item in itemsToCheck)
            {
                bool enabled = character.Retainers.Any(x => item.Ventures.Any(v => v.MatchesJob(x.Job)));
                if (enabled)
                {
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
}
