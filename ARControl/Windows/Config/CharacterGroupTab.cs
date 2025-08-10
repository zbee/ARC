using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using ECommons;

namespace ARControl.Windows.Config;

internal sealed class CharacterGroupTab : ITab
{
    private readonly ConfigWindow _configWindow;
    private readonly Configuration _configuration;
    private readonly Dictionary<Guid, TemporaryConfig> _currentEditPopups = new();

    private TemporaryConfig _newGroup = new() { Name = string.Empty };

    public CharacterGroupTab(ConfigWindow configWindow, Configuration configuration)
    {
        _configWindow = configWindow;
        _configuration = configuration;
    }

    public void Draw()
    {
        using var tab = ImRaii.TabItem("Groups###TabGroups");
        if (!tab)
            return;

        Configuration.CharacterGroup? groupToDelete = null;
        foreach (var group in _configuration.CharacterGroups)
        {
            ImGui.PushID($"##Group{group.Id}");

            if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog))
            {
                _currentEditPopups[group.Id] = new TemporaryConfig
                {
                    Name = group.Name,
                };
                ImGui.OpenPopup($"##EditGroup{group.Id}");
            }

            DrawCharacterGroupEditorPopup(group, out var assignedCharacters, ref groupToDelete);
            ImGui.SameLine();
            DrawCharacterGroup(group, assignedCharacters);

            ImGui.PopID();
        }

        if (groupToDelete != null)
        {
            _configuration.CharacterGroups.Remove(groupToDelete);
            _configWindow.ShouldSave();
        }

        ImGui.Separator();
        DrawNewCharacterGroup();
    }

    private void DrawCharacterGroupEditorPopup(Configuration.CharacterGroup group,
        out List<Configuration.CharacterConfiguration> assignedCharacters,
        ref Configuration.CharacterGroup? groupToDelete)
    {
        assignedCharacters = _configuration.Characters
            .Where(x => x.Type == Configuration.CharacterType.PartOfCharacterGroup &&
                        x.CharacterGroupId == group.Id)
            .OrderBy(x => x.WorldName)
            .ThenBy(x => x.LocalContentId)
            .ToList();
        if (_currentEditPopups.TryGetValue(group.Id, out TemporaryConfig? temporaryConfig) &&
            ImGui.BeginPopup($"##EditGroup{group.Id}"))
        {
            (bool save, bool canSave) = DrawGroupEditor(temporaryConfig, group);

            ImGui.BeginDisabled(!canSave || group.Name == temporaryConfig.Name);
            save |= ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Save, "Save");
            ImGui.EndDisabled();

            if (save && canSave)
            {
                group.Name = temporaryConfig.Name;

                ImGui.CloseCurrentPopup();
                _configWindow.ShouldSave();
            }
            else
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(assignedCharacters.Count > 0);
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Times, "Delete"))
                {
                    groupToDelete = group;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndDisabled();
                if (assignedCharacters.Count > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.BeginTooltip();
                    ImGui.Text(
                        $"Remove the {assignedCharacters.Count} character(s) from this group before deleting it.");
                    foreach (var character in assignedCharacters)
                        ImGui.BulletText($"{character.CharacterName} @ {character.WorldName}");
                    ImGui.EndTooltip();
                }
            }

            ImGui.EndPopup();
        }
    }

    private void DrawCharacterGroup(Configuration.CharacterGroup group,
        List<Configuration.CharacterConfiguration> assignedCharacters)
    {
        string countLabel = assignedCharacters.Count == 0 ? "no characters"
            : assignedCharacters.Count == 1 ? "1 character"
            : $"{assignedCharacters.Count} characters";
        if (ImGui.CollapsingHeader($"{group.Name} ({countLabel})"))
        {
            ImGui.Indent(_configWindow.MainIndentSize);
            using (var tabBar = ImRaii.TabBar("GroupOptions"))
            {
                if (tabBar)
                {
                    using (var ventureListTab = ImRaii.TabItem("Venture Lists"))
                    {
                        if (ventureListTab)
                            _configWindow.DrawVentureListSelection(group.Id.ToString(), group.ItemListIds);
                    }

                    using (var charactersTab = ImRaii.TabItem("Characters"))
                    {
                        if (charactersTab)
                        {
                            ImGui.Text("Characters in this group:");
                            ImGui.Indent(_configWindow.MainIndentSize);
                            foreach (var character in assignedCharacters.OrderBy(x => x.WorldName)
                                         .ThenBy(x => x.LocalContentId))
                                ImGui.TextUnformatted($"{character.CharacterName} @ {character.WorldName}");
                            ImGui.Unindent(_configWindow.MainIndentSize);
                        }
                    }
                }
            }

            ImGui.Unindent(_configWindow.MainIndentSize);
        }
    }

    private void DrawNewCharacterGroup()
    {
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "Add Group"))
            ImGui.OpenPopup("##AddGroup");

        if (ImGui.BeginPopup("##AddGroup"))
        {
            (bool save, bool canSave) = DrawGroupEditor(_newGroup, null);

            ImGui.BeginDisabled(!canSave);
            save |= ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Save, "Save");
            ImGui.EndDisabled();

            if (save && canSave)
            {
                _configuration.CharacterGroups.Add(new Configuration.CharacterGroup
                {
                    Id = Guid.NewGuid(),
                    Name = _newGroup.Name,
                    ItemListIds = new(),
                });

                _newGroup = new() { Name = string.Empty };

                ImGui.CloseCurrentPopup();
                _configWindow.ShouldSave();
            }

            ImGui.EndPopup();
        }
    }

    private (bool Save, bool CanSave) DrawGroupEditor(TemporaryConfig group,
        Configuration.CharacterGroup? existingGroup)
    {
        string name = group.Name;
        bool save = ImGui.InputTextWithHint("", "Group Name...", ref name, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        bool canSave = IsValidGroupName(name, existingGroup);

        group.Name = name;
        return (save, canSave);
    }

    private bool IsValidGroupName(string name, Configuration.CharacterGroup? existingGroup)
    {
        return name.Length >= 2 &&
               !name.Contains('%', StringComparison.Ordinal) &&
               !_configuration.CharacterGroups.Any(x => x != existingGroup && name.EqualsIgnoreCase(x.Name));
    }

    private sealed class TemporaryConfig
    {
        public required string Name { get; set; }
    }
}
