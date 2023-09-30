using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ARControl.GameData;
using Dalamud.Game.ClientState;
using Dalamud.Game.Command;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Style;
using Dalamud.Interface.Windowing;
using Dalamud.Logging;
using Dalamud.Plugin;
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
    private readonly ClientState _clientState;
    private readonly CommandManager _commandManager;

    private string _searchString = string.Empty;
    private Configuration.QueuedItem? _dragDropSource;
    private bool _enableDragDrop;
    private bool _checkPerCharacter = true;
    private bool _onlyShowMissing = true;

    public ConfigWindow(
        DalamudPluginInterface pluginInterface,
        Configuration configuration,
        GameCache gameCache,
        ClientState clientState,
        CommandManager commandManager)
        : base("ARC###ARControlConfig")
    {
        _pluginInterface = pluginInterface;
        _configuration = configuration;
        _gameCache = gameCache;
        _clientState = clientState;
        _commandManager = commandManager;
    }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("ARConfigTabs"))
        {
            DrawItemQueue();
            DrawCharacters();
            DrawGatheredItemsToCheck();
            ImGui.EndTabBar();
        }
    }

    private unsafe void DrawItemQueue()
    {
        if (ImGui.BeginTabItem("Venture Queue"))
        {
            if (ImGui.BeginCombo("Venture...##VentureSelection", ""))
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

            ImGui.Checkbox("Enable Drag&Drop", ref _enableDragDrop);
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
                if (ImGui.BeginPopup($"###ctx{i}"))
                {
                    if (ImGui.Selectable($"Remove {venture.Name}"))
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
                PluginLog.Information($"Updating {itemToAdd.ItemId} → {indexToAdd}");
                _configuration.QueuedItems.Remove(itemToAdd);
                _configuration.QueuedItems.Insert(indexToAdd, itemToAdd);
                Save();
            }

            ImGui.Unindent(30);
            ImGui.EndTabItem();
        }
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
                ImGui.CollapsingHeader(world.Key, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.Bullet);
                foreach (var character in world)
                {
                    ImGui.PushID($"Char{character.LocalContentId}");

                    ImGui.PushItemWidth(ImGui.GetFontSize() * 30);
                    Vector4 buttonColor = new Vector4();
                    if (character.Managed && character.Retainers.Count > 0)
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
                        character.Managed = !character.Managed;
                        Save();
                    }

                    ImGui.SameLine();

                    if (ImGui.CollapsingHeader(
                            $"{character.CharacterName} {(character.Managed ? $"({character.Retainers.Count(x => x.Managed)} / {character.Retainers.Count})" : "")}###{character.LocalContentId}"))
                    {
                        ImGui.Indent(30);
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

                        ImGui.Unindent(30);
                    }

                    ImGui.PopID();
                }
            }

            ImGui.EndTabItem();
        }
    }

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
                    if (ImGuiComponents.IconButton($"SwitchChacters{ch.Character.LocalContentId}",
                            FontAwesomeIcon.DoorOpen))
                    {
                        _commandManager.ProcessCommand($"/ays relog {ch.Character.CharacterName}@{ch.Character.WorldName}");
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
