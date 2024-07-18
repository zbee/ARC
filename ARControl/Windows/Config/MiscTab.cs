using System;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using ImGuiNET;

namespace ARControl.Windows.Config;

internal sealed class MiscTab : ITab
{
    private readonly ConfigWindow _configWindow;
    private readonly Configuration _configuration;

    public MiscTab(ConfigWindow configWindow, Configuration configuration)
    {
        _configWindow = configWindow;
        _configuration = configuration;
    }

    public void Draw()
    {
        using var tab = ImRaii.TabItem("Misc###TabMisc");
        if (!tab)
            return;

        ImGui.Text("Venture Settings");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(130);
        int venturesToKeep = _configuration.Misc.VenturesToKeep;
        if (ImGui.InputInt("Minimum Ventures needed to assign retainers", ref venturesToKeep))
        {
            _configuration.Misc.VenturesToKeep = Math.Max(0, Math.Min(65000, venturesToKeep));
            _configWindow.ShouldSave();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            $"If you have less than {venturesToKeep} ventures, retainers will only be sent out for Quick Ventures (instead of picking the next item from the Venture List).");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text("User Interface Settings");

        bool showAssignmentChatMessages = _configuration.ConfigUiOptions.ShowAssignmentChatMessages;
        if (ImGui.Checkbox("Show chat message when assigning a venture to a retainer",
                ref showAssignmentChatMessages))
        {
            _configuration.ConfigUiOptions.ShowAssignmentChatMessages = showAssignmentChatMessages;
            _configWindow.ShouldSave();
        }

        bool showContents = _configuration.ConfigUiOptions.ShowVentureListContents;
        if (ImGui.Checkbox("Show Venture List preview in Groups/Retainer tabs", ref showContents))
        {
            _configuration.ConfigUiOptions.ShowVentureListContents = showContents;
            _configWindow.ShouldSave();
        }
    }
}
