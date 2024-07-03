using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using ARControl.External;
using ARControl.GameData;
using ARControl.Windows;
using AutoRetainerAPI;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using FFXIVClientStructs.FFXIV.Client.Game;
using ImGuiNET;
using LLib;

namespace ARControl;

[SuppressMessage("ReSharper", "UnusedType.Global")]
public sealed partial class AutoRetainerControlPlugin : IDalamudPlugin
{
    private const int QuickVentureId = 395;
    private readonly WindowSystem _windowSystem = new(nameof(AutoRetainerControlPlugin));

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IClientState _clientState;
    private readonly IChatGui _chatGui;
    private readonly ICommandManager _commandManager;
    private readonly IPluginLog _pluginLog;

    private readonly Configuration _configuration;
    private readonly GameCache _gameCache;
    private readonly IconCache _iconCache;
    private readonly VentureResolver _ventureResolver;
    private readonly AllaganToolsIpc _allaganToolsIpc;
    private readonly ConfigWindow _configWindow;
    private readonly AutoRetainerApi _autoRetainerApi;
    private readonly AutoRetainerReflection _autoRetainerReflection;

    public AutoRetainerControlPlugin(IDalamudPluginInterface pluginInterface, IDataManager dataManager,
        IClientState clientState, IChatGui chatGui, ICommandManager commandManager, ITextureProvider textureProvider,
        IFramework framework, IPluginLog pluginLog)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        ArgumentNullException.ThrowIfNull(dataManager);

        _pluginInterface = pluginInterface;
        _clientState = clientState;
        _chatGui = chatGui;
        _commandManager = commandManager;
        _pluginLog = pluginLog;

        _configuration = (Configuration?)_pluginInterface.GetPluginConfig() ?? new Configuration { Version = 2 };

        _gameCache = new GameCache(dataManager);
        _iconCache = new IconCache(textureProvider);
        _ventureResolver = new VentureResolver(_gameCache, _pluginLog);
        DiscardHelperIpc discardHelperIpc = new(_pluginInterface);
        _allaganToolsIpc = new AllaganToolsIpc(pluginInterface, pluginLog);
        _configWindow =
            new ConfigWindow(_pluginInterface, _configuration, _gameCache, _clientState, _commandManager, _iconCache,
                discardHelperIpc, _pluginLog);
        _windowSystem.AddWindow(_configWindow);

        ECommonsMain.Init(_pluginInterface, this);
        _autoRetainerApi = new();
        _autoRetainerReflection = new AutoRetainerReflection(pluginInterface, framework, pluginLog, _autoRetainerApi);

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
        {
            try
            {
                Sync();
            }
            catch (Exception e)
            {
                _pluginLog.Error(e, "Unable to sync characters");
                _chatGui.PrintError(
                    "Unable to synchronize characters with AutoRetainer, plugin might not work properly.");
            }
        }
    }

    private void SendRetainerToVenture(string retainerName)
    {
        var venture = GetNextVenture(retainerName, false);
        if (venture.HasValue)
            _autoRetainerApi.SetVenture(venture.Value);
    }

    private unsafe uint? GetNextVenture(string retainerName, bool dryRun)
    {
        if (!_autoRetainerReflection.ShouldReassign)
        {
            _pluginLog.Information("AutoRetainer is configured to not reassign ventures, so we are not checking any venture lists.");
            return null;
        }

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

        if (ch.Ventures == 0)
        {
            _pluginLog.Warning(
                "Could not assign a next venture from venture list, as the character has no ventures left.");
        }
        else if (ch.Ventures <= _configuration.Misc.VenturesToKeep)
        {
            _pluginLog.Warning(
                $"Could not assign a next venture from venture list, character only has {ch.Ventures} left, configuration says to only send out above {_configuration.Misc.VenturesToKeep} ventures.");
        }
        else
        {
            var venturesInProgress = CalculateVenturesInProgress(ch);
            foreach (var inProgress in venturesInProgress)
            {
                _pluginLog.Verbose(
                    $"Venture In Progress: ItemId {inProgress.Key} for a total amount of {inProgress.Value}");
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
                                             venturesInProgress.GetValueOrDefault(x.ItemId, 0) +
                                             (list.CheckRetainerInventory
                                                 ? (int)_allaganToolsIpc.GetRetainerItemCount(x.ItemId)
                                                 : 0),
                        })
                        .Where(x => x.InventoryCount < x.RequestedCount)
                        .ToList()
                        .AsReadOnly();

                    // collect items with the least current inventory first
                    if (list.Priority == Configuration.ListPriority.Balanced)
                        itemsOnList = itemsOnList.OrderBy(x => x.InventoryCount).ToList().AsReadOnly();
                }

                _pluginLog.Debug($"Found {itemsOnList.Count} to-do items on current list");
                if (itemsOnList.Count == 0)
                    continue;

                foreach (var itemOnList in itemsOnList)
                {
                    _pluginLog.Debug($"Checking venture info for itemId {itemOnList.ItemId}");

                    var (venture, reward) = _ventureResolver.ResolveVenture(ch, retainer, itemOnList.ItemId);
                    if (venture == null)
                    {
                        venture = _gameCache.Ventures.FirstOrDefault(x => x.ItemId == itemOnList.ItemId);
                        _pluginLog.Debug(
                            $"Retainer doesn't know how to gather itemId {itemOnList.ItemId} ({venture?.Name})");
                    }
                    else if (reward == null)
                    {
                        _pluginLog.Debug($"Retainer can't complete venture '{venture.Name}'");
                    }
                    else
                    {
                        if (_configuration.ConfigUiOptions.ShowAssignmentChatMessages || dryRun)
                            PrintNextVentureMessage(retainerName, venture, reward, list);

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
        }

        // fallback: managed but no venture found/
        if (retainer.LastVenture != QuickVentureId)
        {
            PrintEndOfListMessage(retainerName, retainer);
            if (!dryRun)
            {
                retainer.HasVenture = true;
                retainer.LastVenture = QuickVentureId;
                _pluginInterface.SavePluginConfig(_configuration);
            }

            // Unsure if this (eventually) will do venture plans you've configured in AutoRetainer, but by default
            // (with Assign + Reassign) as config options, returning `0` here as suggested in
            // https://discord.com/channels/1001823907193552978/1001825038598676530/1161295221447983226
            // will just repeat the last venture.
            //
            // That makes sense, of course, but it's also not really the desired behaviour for when you're at the end
            // of a list.
            return QuickVentureId;
        }
        else
        {
            _pluginLog.Information("Not changing venture, already a quick venture");
            return null;
        }
    }

    private void PrintNextVentureMessage(string retainerName, Venture venture, VentureReward reward, Configuration.ItemList list)
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
    }

    private void PrintEndOfListMessage(string retainerName, Configuration.RetainerConfiguration retainer)
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
        _pluginLog.Information($"No tasks left (previous venture = {retainer.LastVenture}), using QV");
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
        ImGui.Text(SeIconChar.Collectible.ToIconString());
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
        else if (arguments.StartsWith("dnv", StringComparison.Ordinal))
        {
            var ch = _configuration.Characters.SingleOrDefault(x => x.LocalContentId == _clientState.LocalContentId);
            if (ch == null || ch.Type == Configuration.CharacterType.NotManaged || ch.Retainers.Count == 0)
            {
                _chatGui.PrintError("No character to debug.");
                return;
            }

            string[] s = arguments.Split(" ");
            string? retainerName;
            if (s.Length > 1)
                retainerName = ch.Retainers.SingleOrDefault(x => x.Name.EqualsIgnoreCase(s[1]))?.Name;
            else
                retainerName = ch.Retainers
                    .OrderBy(x => x.DisplayOrder)
                    .ThenBy(x => x.RetainerContentId)
                    .FirstOrDefault()?.Name;

            if (retainerName == null)
            {
                if (s.Length > 1)
                    _chatGui.PrintError($"Could not find retainer {s[1]}.");
                else
                    _chatGui.PrintError("Could not find retainer.");
                return;
            }

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
        _autoRetainerReflection.Dispose();
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
