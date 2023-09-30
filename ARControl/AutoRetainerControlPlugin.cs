using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using ARControl.GameData;
using ARControl.Windows;
using AutoRetainerAPI;
using Dalamud.Data;
using Dalamud.Game.ClientState;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using Dalamud.Logging;
using Dalamud.Plugin;
using ECommons;
using ImGuiNET;

namespace ARControl;

[SuppressMessage("ReSharper", "UnusedType.Global")]
public sealed partial class AutoRetainerControlPlugin : IDalamudPlugin
{
    private readonly WindowSystem _windowSystem = new(nameof(AutoRetainerControlPlugin));

    private readonly DalamudPluginInterface _pluginInterface;
    private readonly ClientState _clientState;
    private readonly ChatGui _chatGui;
    private readonly CommandManager _commandManager;

    private readonly Configuration _configuration;
    private readonly GameCache _gameCache;
    private readonly VentureResolver _ventureResolver;
    private readonly ConfigWindow _configWindow;
    private readonly AutoRetainerApi _autoRetainerApi;

    public AutoRetainerControlPlugin(DalamudPluginInterface pluginInterface, DataManager dataManager,
        ClientState clientState, ChatGui chatGui, CommandManager commandManager)
    {
        _pluginInterface = pluginInterface;
        _clientState = clientState;
        _chatGui = chatGui;
        _commandManager = commandManager;

        _configuration = (Configuration?)_pluginInterface.GetPluginConfig() ?? new Configuration();

        _gameCache = new GameCache(dataManager);
        _ventureResolver = new VentureResolver(_gameCache);
        _configWindow = new ConfigWindow(_pluginInterface, _configuration, _gameCache, _clientState, _commandManager);
        _windowSystem.AddWindow(_configWindow);

        ECommonsMain.Init(_pluginInterface, this);
        _autoRetainerApi = new();

        _pluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenConfigUi += _configWindow.Toggle;
        _autoRetainerApi.OnSendRetainerToVenture += SendRetainerToVenture;
        _autoRetainerApi.OnRetainerPostVentureTaskDraw += RetainerTaskButtonDraw;
        _clientState.TerritoryChanged += TerritoryChanged;
        _commandManager.AddHandler("/arc", new CommandInfo(ProcessCommand));

        if (_autoRetainerApi.Ready)
            Sync();
    }

    public string Name => "ARC";

    private void SendRetainerToVenture(string retainerName)
    {
        var ch = _configuration.Characters.SingleOrDefault(x => x.LocalContentId == _clientState.LocalContentId);
        if (ch == null)
        {
            PluginLog.Information("No character information found");
        }
        else if (!ch.Managed)
        {
            PluginLog.Information("Character is not managed");
        }
        else
        {
            var retainer = ch.Retainers.SingleOrDefault(x => x.Name == retainerName);
            if (retainer == null)
            {
                PluginLog.Information("No retainer information found");
            }
            else if (!retainer.Managed)
            {
                PluginLog.Information("Retainer is not managed");
            }
            else
            {
                PluginLog.Information("Checking tasks...");
                Sync();
                foreach (var queuedItem in _configuration.QueuedItems.Where(x => x.RemainingQuantity > 0))
                {
                    PluginLog.Information($"Checking venture info for itemId {queuedItem.ItemId}");

                    var (venture, reward) = _ventureResolver.ResolveVenture(ch, retainer, queuedItem);
                    if (reward == null)
                    {
                        PluginLog.Information("Retainer can't complete venture");
                    }
                    else
                    {
                        _chatGui.Print(
                            $"ARC → Overriding venture to collect {reward.Quantity}x {venture!.Name}.");
                        PluginLog.Information(
                            $"Setting AR to use venture {venture.RowId}, which should retrieve {reward.Quantity}x {venture.Name}");
                        _autoRetainerApi.SetVenture(venture.RowId);

                        retainer.LastVenture = venture.RowId;
                        queuedItem.RemainingQuantity =
                            Math.Max(0, queuedItem.RemainingQuantity - reward.Quantity);
                        _pluginInterface.SavePluginConfig(_configuration);
                        return;
                    }
                }

                // fallback: managed but no venture found
                if (retainer.LastVenture != 395)
                {
                    _chatGui.Print("ARC → No tasks left, using QC");
                    PluginLog.Information($"No tasks left (previous venture = {retainer.LastVenture}), using QC");
                    _autoRetainerApi.SetVenture(395);

                    retainer.LastVenture = 395;
                    _pluginInterface.SavePluginConfig(_configuration);
                }
                else
                    PluginLog.Information("Not changing venture plan, already 395");
            }
        }
    }

    private void RetainerTaskButtonDraw(ulong characterId, string retainerName)
    {
        Configuration.CharacterConfiguration? characterConfiguration =
            _configuration.Characters.FirstOrDefault(x => x.LocalContentId == characterId);
        if (characterConfiguration is not { Managed: true })
            return;

        Configuration.RetainerConfiguration? retainer =
            characterConfiguration.Retainers.FirstOrDefault(x => x.Name == retainerName);
        if (retainer is not { Managed: true })
            return;

        ImGui.SameLine();
        ImGuiComponents.IconButton(FontAwesomeIcon.Book);
    }

    private void TerritoryChanged(object? sender, ushort e) => Sync();

    private void ProcessCommand(string command, string arguments)
    {
        if (arguments == "sync")
            Sync();
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

        _autoRetainerApi.Dispose();
        ECommonsMain.Dispose();
    }
}
