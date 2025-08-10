using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ARControl.GameData;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;

namespace ARControl.Windows.Config;

internal sealed class LockedItemsTab : ITab
{
    private static readonly Vector4 ColorGreen = ImGuiColors.HealerGreen;
    private static readonly Vector4 ColorRed = ImGuiColors.DalamudRed;
    private static readonly Vector4 ColorGrey = ImGuiColors.DalamudGrey;
    private static readonly string CurrentCharPrefix = FontAwesomeIcon.Male.ToIconString();

    private readonly ConfigWindow _configWindow;
    private readonly Configuration _configuration;
    private readonly IClientState _clientState;
    private readonly ICommandManager _commandManager;
    private readonly GameCache _gameCache;

    public LockedItemsTab(ConfigWindow configWindow, Configuration configuration, IClientState clientState,
        ICommandManager commandManager, GameCache gameCache)
    {
        _configWindow = configWindow;
        _configuration = configuration;
        _clientState = clientState;
        _commandManager = commandManager;
        _gameCache = gameCache;
    }

    public void Draw()
    {
        using var tab = ImRaii.TabItem("Locked Items###TabLockedItems");
        if (!tab)
            return;
        bool checkPerCharacter = _configuration.ConfigUiOptions.CheckGatheredItemsPerCharacter;
        if (ImGui.Checkbox("Group by character", ref checkPerCharacter))
        {
            _configuration.ConfigUiOptions.CheckGatheredItemsPerCharacter = checkPerCharacter;
            _configWindow.ShouldSave();
        }

        bool onlyShowMissing = _configuration.ConfigUiOptions.OnlyShowMissingGatheredItems;
        if (ImGui.Checkbox("Only show missing items", ref onlyShowMissing))
        {
            _configuration.ConfigUiOptions.OnlyShowMissingGatheredItems = onlyShowMissing;
            _configWindow.ShouldSave();
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
            DrawPerCharacter(charactersToCheck, itemsToCheck, onlyShowMissing);
        else
            DrawPerItem(charactersToCheck, itemsToCheck, onlyShowMissing);
    }

    private void DrawPerCharacter(List<CheckedCharacter> charactersToCheck, List<CheckedItem> itemsToCheck,
        bool onlyShowMissing)
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
                ImGui.Indent(_configWindow.MainIndentSize + ImGui.GetStyle().FramePadding.X);
                foreach (var item in itemsToCheck.Where(x =>
                             ch.ToCheck(onlyShowMissing).ContainsKey(x.ItemId)))
                {
                    var color = ch.Items[item.ItemId];
                    if (color != ColorGrey)
                    {
                        string itemName = item.GatheredItem.Name;
                        var folkloreBook = _gameCache.FolkloreBooks.Values.FirstOrDefault(x =>
                            x.GatheringItemIds.Contains(item.GatheredItem.GatheredItemId));
                        if (folkloreBook != null && !ch.Character.UnlockedFolkloreBooks.Contains(folkloreBook.ItemId))
                            itemName += $" ({SeIconChar.Prohibited.ToIconString()} {folkloreBook.Name})";

                        ImGui.PushStyleColor(ImGuiCol.Text, color);
                        if (currentCharacter && color == ColorRed)
                        {
                            ImGui.Selectable(itemName);
                            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                            {
                                uint classJob = _clientState.LocalPlayer!.ClassJob.RowId;
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
                            ImGui.Text(itemName);
                        }

                        ImGui.PopStyleColor();
                    }
                }

                ImGui.Unindent(_configWindow.MainIndentSize + ImGui.GetStyle().FramePadding.X);
            }
        }
    }

    private void DrawPerItem(List<CheckedCharacter> charactersToCheck, List<CheckedItem> itemsToCheck,
        bool onlyShowMissing)
    {
        foreach (var item in itemsToCheck.Where(x =>
                     charactersToCheck.Any(y => y.ToCheck(onlyShowMissing).ContainsKey(x.ItemId))))
        {
            var folkloreBook = _gameCache.FolkloreBooks.Values.FirstOrDefault(x =>
                x.GatheringItemIds.Contains(item.GatheredItem.GatheredItemId));
            if (ImGui.CollapsingHeader($"{item.GatheredItem.Name}##Gathered{item.GatheredItem.ItemId}"))
            {
                ImGui.Indent(_configWindow.MainIndentSize + ImGui.GetStyle().FramePadding.X);
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

                        string characterName = ch.Character.ToString();
                        if (folkloreBook != null &&
                            !ch.Character.UnlockedFolkloreBooks.Contains(folkloreBook.ItemId))
                            characterName += $" ({SeIconChar.Prohibited.ToIconString()} {folkloreBook.Name})";

                        ImGui.PushStyleColor(ImGuiCol.Text, color);
                        ImGui.TextUnformatted(characterName);
                        ImGui.PopStyleColor();
                    }
                }

                ImGui.Unindent(_configWindow.MainIndentSize + ImGui.GetStyle().FramePadding.X);
            }
        }
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
                bool enabled = character.Retainers.Any(x => item.Ventures.Any(v => v.CategoryType != EVentureCategoryType.DoWM && v.MatchesJob(x.Job)));
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
}
