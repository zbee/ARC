using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using ARControl.External;
using ARControl.GameData;
using ARControl.Windows.Config;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
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
    public const byte MinLevel = 10;

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Configuration _configuration;
    private readonly GameCache _gameCache;
    private readonly IPluginLog _pluginLog;

    private readonly List<ITab> _tabs;

    private bool _shouldSave;

    public ConfigWindow(
        IDalamudPluginInterface pluginInterface,
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
        _pluginLog = pluginLog;

        _tabs =
        [
            new VentureListTab(this, _configuration, gameCache, iconCache, discardHelperIpc, pluginLog),
            new CharacterGroupTab(this, _configuration),
            new RetainersTab(this, _configuration, iconCache),
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

    public override void Draw()
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

    internal DragDropData CalculateDragDropData(int itemCount)
    {
        float yDelta = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y;
        var firstCursorPos = ImGui.GetCursorScreenPos() +
                             new Vector2(-MainIndentSize, -ImGui.GetStyle().ItemSpacing.Y / 2);
        var lastCursorPos = new Vector2(
            firstCursorPos.X + MainIndentSize + ImGui.GetContentRegionAvail().X,
            firstCursorPos.Y + yDelta * itemCount);

        List<(Vector2 TopLeft, Vector2 BottomRight)> itemPositions = [];
        for (int i = 0; i < itemCount; ++i)
        {
            Vector2 left = firstCursorPos;
            Vector2 right = lastCursorPos with { Y = firstCursorPos.Y + yDelta - 1 };
            itemPositions.Add((left, right));

            firstCursorPos.Y += yDelta;
            lastCursorPos.Y += yDelta;
        }

        return new DragDropData(itemPositions);
    }

    internal sealed record DragDropData(List<(Vector2 TopLeft, Vector2 BottomRight)> ItemPositions);
}
