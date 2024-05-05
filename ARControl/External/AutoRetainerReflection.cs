using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AutoRetainerAPI;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using LLib;

namespace ARControl.External
{
    internal sealed class AutoRetainerReflection : IDisposable
    {
        private readonly IPluginLog _pluginLog;
        private readonly AutoRetainerApi _autoRetainerApi;
        private readonly DalamudReflector _reflector;

        public AutoRetainerReflection(DalamudPluginInterface pluginInterface, IFramework framework,
            IPluginLog pluginLog, AutoRetainerApi autoRetainerApi)
        {
            _pluginLog = pluginLog;
            _autoRetainerApi = autoRetainerApi;
            _reflector = new DalamudReflector(pluginInterface, framework, pluginLog);
        }

        [SuppressMessage("Performance", "CA2000", Justification = "Should not dispose other plugins")]
        public bool ShouldReassign
        {
            get
            {
                try
                {
                    if (_autoRetainerApi.Ready &&
                        _reflector.TryGetDalamudPlugin("AutoRetainer", out var autoRetainer, false, true))
                    {
                        var config =
                            autoRetainer.GetType().GetProperty("C", BindingFlags.Static | BindingFlags.NonPublic)!
                                .GetValue(null);
                        if (config == null)
                        {
                            _pluginLog.Warning("Could not retrieve AR config");
                            return true;
                        }

                        bool dontReassign = (bool)config.GetType()
                            .GetField("_dontReassign", BindingFlags.Instance | BindingFlags.Public)!
                            .GetValue(config)!;
                        _pluginLog.Verbose($"DontReassign is set to {dontReassign}");
                        return !dontReassign;
                    }


                    _pluginLog.Warning("Could not check if reassign is enabled, AutoRetainer not loaded");
                    return true;
                }
                catch (Exception e)
                {
                    _pluginLog.Warning(e, "Unable to check if reassign is enabled");
                    return true;
                }
            }
        }

        public void Dispose()
        {
            _reflector.Dispose();
        }
    }
}
