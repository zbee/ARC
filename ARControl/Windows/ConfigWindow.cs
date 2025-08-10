using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ARControl.External;
using ARControl.GameData;
using ARControl.Windows.Config;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using LLib.ImGui;

namespace ARControl.Windows;

internal sealed class ConfigWindow : LWindow
{
    public const byte MinLevel = 10;

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Configuration _configuration;
    private readonly GameCache _gameCache;
    private readonly IPluginLog _pluginLog;

    private readonly List<ITab> _tabs;

    private bool _shouldSave;
    private (string, int)? _draggedItem;

    public ConfigWindow(
        IDalamudPluginInterface pluginInterface,
        Configuration configuration,
        GameCache gameCache,
        IClientState clientState,
        ICommandManager commandManager,
        ITextureProvider textureProvider,
        DiscardHelperIpc discardHelperIpc,
        AllaganToolsIpc allaganToolsIpc,
        IPluginLog pluginLog)
        : base($"ARC {SeIconChar.Collectible.ToIconString()}###ARControlConfig")
    {
        _pluginInterface = pluginInterface;
        _configuration = configuration;
        _gameCache = gameCache;
        _pluginLog = pluginLog;

        _tabs =
        [
            new VentureListTab(this, _configuration, gameCache, textureProvider, discardHelperIpc, pluginLog),
            new CharacterGroupTab(this, _configuration),
            new RetainersTab(this, _configuration, textureProvider),
            new InventoryTab(_configuration, allaganToolsIpc, _gameCache, pluginLog),
            new LockedItemsTab(this, _configuration, clientState, commandManager, gameCache),
            new MiscTab(this, _configuration),
        ];

        SizeConstraints = new()
        {
            MinimumSize = new Vector2(480, 300),
            MaximumSize = new Vector2(9999, 9999),
        };
    }

    public float MainIndentSize { get; private set; } = 1;

    public override void DrawContent()
    {
        using var tabBar = ImRaii.TabBar("ARConfigTabs");
        if (!tabBar)
            return;

        ImGui.PushFont(UiBuilder.IconFont);
        MainIndentSize = ImGui.CalcTextSize(FontAwesomeIcon.Cog.ToIconString()).X +
            ImGui.GetStyle().FramePadding.X * 2f +
            ImGui.GetStyle().ItemSpacing.X - ImGui.GetStyle().WindowPadding.X / 2;
        ImGui.PopFont();

        foreach (var tab in _tabs)
            tab.Draw();

        if (_shouldSave && !ImGui.IsAnyMouseDown())
        {
            _pluginLog.Debug("Triggering delayed save");
            Save();
        }
    }

    internal void DrawVentureListSelection(string id, List<Guid> selectedLists)
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

        float width = ImGui.GetContentRegionAvail().X;
        List<(Vector2 TopLeft, Vector2 BottomRight)> itemPositions = [];

        for (int i = 0; i < selectedLists.Count; ++i)
        {
            Vector2 topLeft = ImGui.GetCursorScreenPos() +
                              new Vector2(-MainIndentSize, -ImGui.GetStyle().ItemSpacing.Y / 2);

            ImGui.PushID($"##{id}_Item{i}");
            var listId = selectedLists[i];
            var listIndex = itemLists.FindIndex(x => x.Id == listId);

            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X -
                                   ImGui.CalcTextSize(FontAwesomeIcon.ArrowsUpDown.ToIconString()).X -
                                   ImGui.CalcTextSize(FontAwesomeIcon.Times.ToIconString()).X -
                                   ImGui.GetStyle().FramePadding.X * 4 -
                                   ImGui.GetStyle().ItemSpacing.X * 2);
            ImGui.PopFont();
            if (ImGui.Combo("", ref listIndex, itemLists.Select(x => x.Name).ToArray(), itemLists.Count))
            {
                selectedLists[i] = itemLists[listIndex].Id;
                Save();
            }

            if (selectedLists.Count > 1)
            {
                ImGui.SameLine();

                if (_draggedItem != null && _draggedItem.Value.Item1 == id && _draggedItem.Value.Item2 == i)
                {
                    ImGuiComponents.IconButton("##Move", FontAwesomeIcon.ArrowsUpDown,
                        ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.ButtonActive)));
                }
                else
                    ImGuiComponents.IconButton("##Move", FontAwesomeIcon.ArrowsUpDown);

                if (_draggedItem == null && ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                    _draggedItem = (id, i);

                ImGui.SameLine();
            }
            else
            {
                ImGui.PushFont(UiBuilder.IconFont);
                ImGui.SameLine(0,
                    ImGui.CalcTextSize(FontAwesomeIcon.ArrowsUpDown.ToIconString()).X +
                    ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().ItemSpacing.X * 2);
                ImGui.PopFont();
            }

            if (ImGuiComponents.IconButton($"##Remove{i}", FontAwesomeIcon.Times))
                itemToRemove = i;

            if (listIndex > 0)
            {
                if (selectedLists.Take(i).Any(x => x == listId))
                {
                    ImGui.Indent(MainIndentSize);
                    ImGui.TextColored(ImGuiColors.DalamudYellow, "This entry is a duplicate and will be ignored.");
                    ImGui.Unindent(MainIndentSize);
                }
                else if (_configuration.ConfigUiOptions.ShowVentureListContents)
                {
                    var list = itemLists[listIndex].List;
                    ImGui.Indent(MainIndentSize);
                    ImGui.Text(list.Type == Configuration.ListType.CollectOneTime
                        ? "Items on this list will be collected once."
                        : "Items on this list will be kept in stock on each character.");
                    ImGui.Spacing();
                    foreach (var item in list.Items)
                    {
                        var venture = _gameCache.Ventures.First(x => x.ItemId == item.ItemId);
                        ImGui.Text($"{item.RemainingQuantity}x {venture.Name}");
                    }

                    ImGui.Unindent(MainIndentSize);
                }
            }

            ImGui.PopID();

            Vector2 bottomRight = new Vector2(topLeft.X + width + MainIndentSize,
                ImGui.GetCursorScreenPos().Y - ImGui.GetStyle().ItemSpacing.Y + 2);
            //ImGui.GetWindowDrawList().AddRect(topLeft, bottomRight, ImGui.GetColorU32(i % 2 == 0 ? ImGuiColors.DalamudRed : ImGuiColors.HealerGreen));
            itemPositions.Add((topLeft, bottomRight));
        }

        if (!ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            _draggedItem = null;
        else if (_draggedItem != null && _draggedItem.Value.Item1 == id)
        {
            int oldIndex = _draggedItem.Value.Item2;
            var draggedItem = selectedLists[oldIndex];

            var (topLeft, bottomRight) = itemPositions[oldIndex];
            topLeft += new Vector2(MainIndentSize, 0);
            ImGui.GetWindowDrawList().AddRect(topLeft, bottomRight, ImGui.GetColorU32(ImGuiColors.DalamudGrey), 3f,
                ImDrawFlags.RoundCornersAll);

            int newIndex = itemPositions.IndexOf(x => ImGui.IsMouseHoveringRect(x.TopLeft, x.BottomRight, true));
            if (newIndex >= 0 && oldIndex != newIndex)
            {
                itemToAdd = _draggedItem.Value.Item2;
                indexToAdd = newIndex;

                _draggedItem = (_draggedItem.Value.Item1, newIndex);
            }
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
        _shouldSave = false;
    }

    public void ShouldSave() => _shouldSave = true;
}
