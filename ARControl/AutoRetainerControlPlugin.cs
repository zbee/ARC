using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using ARControl.GameData;
using ARControl.Windows;
using AutoRetainerAPI;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using FFXIVClientStructs.FFXIV.Client.Game;
using ImGuiNET;

namespace ARControl;

[SuppressMessage("ReSharper", "UnusedType.Global")]
public sealed partial class AutoRetainerControlPlugin : IDalamudPlugin
{
    private const int QuickVentureId = 395;
    private readonly WindowSystem _windowSystem = new(nameof(AutoRetainerControlPlugin));

    private readonly DalamudPluginInterface _pluginInterface;
    private readonly IClientState _clientState;
    private readonly IChatGui _chatGui;
    private readonly ICommandManager _commandManager;
    private readonly IPluginLog _pluginLog;

    private readonly Configuration _configuration;
    private readonly GameCache _gameCache;
    private readonly IconCache _iconCache;
    private readonly VentureResolver _ventureResolver;
    private readonly ConfigWindow _configWindow;
    private readonly AutoRetainerApi _autoRetainerApi;

    public AutoRetainerControlPlugin(DalamudPluginInterface pluginInterface, IDataManager dataManager,
        IClientState clientState, IChatGui chatGui, ICommandManager commandManager, ITextureProvider textureProvider,
        IPluginLog pluginLog)
    {
        _pluginInterface = pluginInterface;
        _clientState = clientState;
        _chatGui = chatGui;
        _commandManager = commandManager;
        _pluginLog = pluginLog;

        _configuration = (Configuration?)_pluginInterface.GetPluginConfig() ?? new Configuration { Version = 2 };

        _gameCache = new GameCache(dataManager);
        _iconCache = new IconCache(textureProvider);
        _ventureResolver = new VentureResolver(_gameCache, _pluginLog);
        _configWindow =
            new ConfigWindow(_pluginInterface, _configuration, _gameCache, _clientState, _commandManager, _iconCache,
                    _pluginLog)
                { IsOpen = true };
        _windowSystem.AddWindow(_configWindow);

        ECommonsMain.Init(_pluginInterface, this);
        _autoRetainerApi = new();

        _pluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenConfigUi += _configWindow.Toggle;
        _autoRetainerApi.OnSendRetainerToVenture += SendRetainerToVenture;
        _autoRetainerApi.OnRetainerPostVentureTaskDraw += RetainerTaskButtonDraw;
        _clientState.TerritoryChanged += TerritoryChanged;
        _commandManager.AddHandler("/arc", new CommandInfo(ProcessCommand)
        {
            HelpMessage = "Manage retainers"
        });

        if (_autoRetainerApi.Ready)
            Sync();
    }

    private void SendRetainerToVenture(string retainerName)
    {
        var venture = GetNextVenture(retainerName, false);
        if (venture == QuickVentureId)
            _autoRetainerApi.SetVenture(0);
        else if (venture.HasValue)
            _autoRetainerApi.SetVenture(venture.Value);
    }

    private unsafe uint? GetNextVenture(string retainerName, bool dryRun)
    {
        var ch = _configuration.Characters.SingleOrDefault(x => x.LocalContentId == _clientState.LocalContentId);
        if (ch == null)
        {
            _pluginLog.Information("No character information found");
            return null;
        }

        if (ch.Type == Configuration.CharacterType.NotManaged)
        {
            _pluginLog.Information("Character is not managed");
            return null;
        }

        var retainer = ch.Retainers.SingleOrDefault(x => x.Name == retainerName);
        if (retainer == null)
        {
            _pluginLog.Information("No retainer information found");
            return null;
        }

        if (!retainer.Managed)
        {
            _pluginLog.Information("Retainer is not managed");
            return null;
        }

        _pluginLog.Information("Checking tasks...");
        Sync();
        var venturesInProgress = CalculateVenturesInProgress(ch);
        foreach (var inpr in venturesInProgress)
        {
            _pluginLog.Information($"In Progress: {inpr.Key} → {inpr.Value}");
        }

        IReadOnlyList<Guid> itemListIds;
        if (ch.Type == Configuration.CharacterType.Standalone)
            itemListIds = ch.ItemListIds;
        else
        {
            var group = _configuration.CharacterGroups.SingleOrDefault(x => x.Id == ch.CharacterGroupId);
            if (group == null)
            {
                _pluginLog.Error($"Unable to resolve character group {ch.CharacterGroupId}.");
                return null;
            }

            itemListIds = group.ItemListIds;
        }

        var itemLists = itemListIds.Where(listId => listId != Guid.Empty)
            .Select(listId => _configuration.ItemLists.SingleOrDefault(x => x.Id == listId))
            .Where(list => list != null)
            .Cast<Configuration.ItemList>()
            .ToList();
        InventoryManager* inventoryManager = InventoryManager.Instance();
        foreach (var list in itemLists)
        {
            _pluginLog.Information($"Checking ventures in list '{list.Name}'");
            IReadOnlyList<StockedItem> itemsOnList;
            if (list.Type == Configuration.ListType.CollectOneTime)
            {
                itemsOnList = list.Items
                    .Select(x => new StockedItem
                    {
                        QueuedItem = x,
                        InventoryCount = 0,
                    })
                    .Where(x => x.RequestedCount > 0)
                    .ToList()
                    .AsReadOnly();
            }
            else
            {
                itemsOnList = list.Items
                    .Select(x => new StockedItem
                    {
                        QueuedItem = x,
                        InventoryCount = inventoryManager->GetInventoryItemCount(x.ItemId) +
                                         (venturesInProgress.TryGetValue(x.ItemId, out int inProgress)
                                             ? inProgress
                                             : 0),
                    })
                    .Where(x => x.InventoryCount <= x.RequestedCount)
                    .ToList()
                    .AsReadOnly();

                // collect items with the least current inventory first
                if (list.Priority == Configuration.ListPriority.Balanced)
                    itemsOnList = itemsOnList.OrderBy(x => x.InventoryCount).ToList().AsReadOnly();
            }

            _pluginLog.Information($"Found {itemsOnList.Count} items on current list");
            if (itemsOnList.Count == 0)
                continue;

            foreach (var itemOnList in itemsOnList)
            {
                _pluginLog.Information($"Checking venture info for itemId {itemOnList.ItemId}");

                var (venture, reward) = _ventureResolver.ResolveVenture(ch, retainer, itemOnList.ItemId);
                if (venture == null || reward == null)
                {
                    _pluginLog.Information($"Retainer can't complete venture '{venture?.Name}'");
                }
                else
                {
                    _chatGui.Print(
                        new SeString(new UIForegroundPayload(579))
                            .Append(SeIconChar.Collectible.ToIconString())
                            .Append(new UIForegroundPayload(0))
                            .Append($" Sending retainer ")
                            .Append(new UIForegroundPayload(1))
                            .Append(retainerName)
                            .Append(new UIForegroundPayload(0))
                            .Append(" to collect ")
                            .Append(new UIForegroundPayload(1))
                            .Append($"{reward.Quantity}x ")
                            .Append(new ItemPayload(venture.ItemId))
                            .Append(venture.Name)
                            .Append(RawPayload.LinkTerminator)
                            .Append(new UIForegroundPayload(0))
                            .Append(" for ")
                            .Append(new UIForegroundPayload(1))
                            .Append($"{list.Name} {list.GetIcon()}")
                            .Append(new UIForegroundPayload(0))
                            .Append("."));
                    _pluginLog.Information(
                        $"Setting AR to use venture {venture.RowId}, which should retrieve {reward.Quantity}x {venture.Name}");

                    if (!dryRun)
                    {
                        retainer.HasVenture = true;
                        retainer.LastVenture = venture.RowId;

                        if (list.Type == Configuration.ListType.CollectOneTime)
                        {
                            itemOnList.RequestedCount =
                                Math.Max(0, itemOnList.RequestedCount - reward.Quantity);
                        }

                        _pluginInterface.SavePluginConfig(_configuration);
                    }

                    return venture.RowId;
                }
            }
        }

        // fallback: managed but no venture found
        if (retainer.LastVenture != QuickVentureId)
        {
            _chatGui.Print(
                new SeString(new UIForegroundPayload(579))
                    .Append(SeIconChar.Collectible.ToIconString())
                    .Append(new UIForegroundPayload(0))
                    .Append($" No tasks left for retainer ")
                    .Append(new UIForegroundPayload(1))
                    .Append(retainerName)
                    .Append(new UIForegroundPayload(0))
                    .Append(", sending to ")
                    .Append(new UIForegroundPayload(1))
                    .Append("Quick Venture")
                    .Append(new UIForegroundPayload(0))
                    .Append("."));
            _pluginLog.Information($"No tasks left (previous venture = {retainer.LastVenture}), using QC");

            if (!dryRun)
            {
                retainer.HasVenture = true;
                retainer.LastVenture = QuickVentureId;
                _pluginInterface.SavePluginConfig(_configuration);
            }

            return QuickVentureId;
        }
        else
        {
            _pluginLog.Information("Not changing venture, already a quick venture");
            return null;
        }
    }

    /// <remarks>
    /// This treats the retainer who is currently doing the venture as 'in-progress', since I believe the
    /// relevant event is fired BEFORE the venture rewards are collected.
    /// </remarks>
    private Dictionary<uint, int> CalculateVenturesInProgress(Configuration.CharacterConfiguration character)
    {
        Dictionary<uint, int> inProgress = new Dictionary<uint, int>();
        foreach (var retainer in character.Retainers)
        {
            if (retainer.Managed && retainer.HasVenture && retainer.LastVenture != 0)
            {
                uint ventureId = retainer.LastVenture;
                if (ventureId == 0)
                    continue;

                var ventureForId = _gameCache.Ventures.SingleOrDefault(x => x.RowId == ventureId);
                if (ventureForId == null)
                    continue;

                uint itemId = ventureForId.ItemId;
                var (venture, reward) = _ventureResolver.ResolveVenture(character, retainer, itemId);
                if (venture == null || reward == null)
                    continue;

                if (inProgress.TryGetValue(itemId, out int existingQuantity))
                    inProgress[itemId] = reward.Quantity + existingQuantity;
                else
                    inProgress[itemId] = reward.Quantity;
            }
        }

        return inProgress;
    }

    private void RetainerTaskButtonDraw(ulong characterId, string retainerName)
    {
        Configuration.CharacterConfiguration? characterConfiguration =
            _configuration.Characters.FirstOrDefault(x => x.LocalContentId == characterId);
        if (characterConfiguration is not { Type: not Configuration.CharacterType.NotManaged })
            return;

        Configuration.RetainerConfiguration? retainer =
            characterConfiguration.Retainers.FirstOrDefault(x => x.Name == retainerName);
        if (retainer is not { Managed: true })
            return;

        ImGui.SameLine();
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.Text(FontAwesomeIcon.Book.ToIconString());
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
        {
            string text = "This retainer is managed by ARC.";

            if (characterConfiguration.Type == Configuration.CharacterType.PartOfCharacterGroup)
            {
                var group = _configuration.CharacterGroups.Single(x => x.Id == characterConfiguration.CharacterGroupId);
                text += $"\n\nCharacter Group: {group.Name}";
            }

            ImGui.SetTooltip(text);
        }
    }

    private void TerritoryChanged(ushort e) => Sync();

    private void ProcessCommand(string command, string arguments)
    {
        if (arguments == "sync")
            Sync();
        else if (arguments == "d")
        {
            var ch = _configuration.Characters.SingleOrDefault(x => x.LocalContentId == _clientState.LocalContentId);
            if (ch == null || ch.Type == Configuration.CharacterType.NotManaged || ch.Retainers.Count == 0)
            {
                _chatGui.PrintError("No character to debug.");
                return;
            }

            string retainerName = ch.Retainers.OrderBy(x => x.DisplayOrder).First().Name;
            var venture = GetNextVenture(retainerName, true);
            if (venture == QuickVentureId)
                _chatGui.Print($"Next venture for {retainerName} is Quick Venture.");
            else if (venture.HasValue)
                _chatGui.Print(
                    $"Next venture for {retainerName} is {_gameCache.Ventures.First(x => x.RowId == venture.Value).Name}.");
            else
                _chatGui.Print($"Next venture for {retainerName} is (none).");
        }
        else
            _configWindow.Toggle();
    }

    public void Dispose()
    {
        _commandManager.RemoveHandler("/arc");
        _clientState.TerritoryChanged -= TerritoryChanged;
        _autoRetainerApi.OnRetainerPostVentureTaskDraw -= RetainerTaskButtonDraw;
        _autoRetainerApi.OnSendRetainerToVenture -= SendRetainerToVenture;
        _pluginInterface.UiBuilder.OpenConfigUi -= _configWindow.Toggle;
        _pluginInterface.UiBuilder.Draw -= _windowSystem.Draw;

        _iconCache.Dispose();
        _autoRetainerApi.Dispose();
        ECommonsMain.Dispose();
    }

    private sealed class StockedItem
    {
        public required Configuration.QueuedItem QueuedItem { get; set; }
        public required int InventoryCount { get; set; }
        public uint ItemId => QueuedItem.ItemId;

        public int RequestedCount
        {
            get => QueuedItem.RemainingQuantity;
            set => QueuedItem.RemainingQuantity = value;
        }
    }
}
