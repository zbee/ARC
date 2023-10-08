using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using ARControl.GameData;
using ARControl.Windows;
using AutoRetainerAPI;
using Dalamud.Game.Command;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ImGuiNET;

namespace ARControl;

[SuppressMessage("ReSharper", "UnusedType.Global")]
public sealed partial class AutoRetainerControlPlugin : IDalamudPlugin
{
    private readonly WindowSystem _windowSystem = new(nameof(AutoRetainerControlPlugin));

    private readonly DalamudPluginInterface _pluginInterface;
    private readonly IClientState _clientState;
    private readonly IChatGui _chatGui;
    private readonly ICommandManager _commandManager;
    private readonly IPluginLog _pluginLog;

    private readonly Configuration _configuration;
    private readonly GameCache _gameCache;
    private readonly VentureResolver _ventureResolver;
    private readonly ConfigWindow _configWindow;
    private readonly AutoRetainerApi _autoRetainerApi;

    public AutoRetainerControlPlugin(DalamudPluginInterface pluginInterface, IDataManager dataManager,
        IClientState clientState, IChatGui chatGui, ICommandManager commandManager, IPluginLog pluginLog)
    {
        _pluginInterface = pluginInterface;
        _clientState = clientState;
        _chatGui = chatGui;
        _commandManager = commandManager;
        _pluginLog = pluginLog;

        _configuration = (Configuration?)_pluginInterface.GetPluginConfig() ?? new Configuration { Version = 2 };

        _gameCache = new GameCache(dataManager);
        _ventureResolver = new VentureResolver(_gameCache, _pluginLog);
        _configWindow =
            new ConfigWindow(_pluginInterface, _configuration, _gameCache, _clientState, _commandManager, _pluginLog)
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
        var ch = _configuration.Characters.SingleOrDefault(x => x.LocalContentId == _clientState.LocalContentId);
        if (ch == null)
        {
            _pluginLog.Information("No character information found");
        }
        else if (ch.Type == Configuration.CharacterType.NotManaged)
        {
            _pluginLog.Information("Character is not managed");
        }
        else
        {
            var retainer = ch.Retainers.SingleOrDefault(x => x.Name == retainerName);
            if (retainer == null)
            {
                _pluginLog.Information("No retainer information found");
            }
            else if (!retainer.Managed)
            {
                _pluginLog.Information("Retainer is not managed");
            }
            else
            {
                _pluginLog.Information("Checking tasks...");
                Sync();
                /* FIXME
                foreach (var queuedItem in _configuration.QueuedItems.Where(x => x.RemainingQuantity > 0))
                {
                    _pluginLog.Information($"Checking venture info for itemId {queuedItem.ItemId}");

                    var (venture, reward) = _ventureResolver.ResolveVenture(ch, retainer, queuedItem);
                    if (reward == null)
                    {
                        _pluginLog.Information("Retainer can't complete venture");
                    }
                    else
                    {
                        _chatGui.Print(
                            $"[ARC] Sending retainer {retainerName} to collect {reward.Quantity}x {venture!.Name}.");
                        _pluginLog.Information(
                            $"Setting AR to use venture {venture.RowId}, which should retrieve {reward.Quantity}x {venture.Name}");
                        _autoRetainerApi.SetVenture(venture.RowId);

                        retainer.LastVenture = venture.RowId;
                        queuedItem.RemainingQuantity =
                            Math.Max(0, queuedItem.RemainingQuantity - reward.Quantity);
                        _pluginInterface.SavePluginConfig(_configuration);
                        return;
                    }
                }
                */

                // fallback: managed but no venture found
                if (retainer.LastVenture != 395)
                {
                    _chatGui.Print($"[ARC] No tasks left for retainer {retainerName}, sending to Quick Venture.");
                    _pluginLog.Information($"No tasks left (previous venture = {retainer.LastVenture}), using QC");
                    _autoRetainerApi.SetVenture(395);

                    retainer.LastVenture = 395;
                    _pluginInterface.SavePluginConfig(_configuration);
                }
                else
                    _pluginLog.Information("Not changing venture plan, already 395");
            }
        }
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
