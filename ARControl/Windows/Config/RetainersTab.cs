using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Common.Math;
using ImGuiNET;
using LLib;

namespace ARControl.Windows.Config;

internal sealed class RetainersTab : ITab
{
    private readonly ConfigWindow _configWindow;
    private readonly Configuration _configuration;
    private readonly IconCache _iconCache;

    public RetainersTab(ConfigWindow configWindow, Configuration configuration, IconCache iconCache)
    {
        _configWindow = configWindow;
        _configuration = configuration;
        _iconCache = iconCache;
    }

    public void Draw()
    {
        using var tab = ImRaii.TabItem("Retainers###TabRetainers");
        if (!tab)
            return;

        foreach (var world in _configuration.Characters
                     .Where(x => x.Retainers.Any(y => y.Job != 0))
                     .OrderBy(x => x.LocalContentId)
                     .GroupBy(x => x.WorldName))
        {
            ImGui.CollapsingHeader(world.Key,
                ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.Bullet);
            foreach (var character in world)
            {
                ImGui.PushID($"Char{character.LocalContentId}");

                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 30);
                Vector4 buttonColor = ImGui.ColorConvertU32ToFloat4(ImGui.GetColorU32(ImGuiCol.FrameBg));
                if (character is { Type: not Configuration.CharacterType.NotManaged, Retainers.Count: > 0 })
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
                    if (character.Type == Configuration.CharacterType.NotManaged)
                    {
                        character.Type = Configuration.CharacterType.Standalone;
                        character.CharacterGroupId = Guid.Empty;
                    }
                    else
                    {
                        character.Type = Configuration.CharacterType.NotManaged;
                        character.CharacterGroupId = Guid.Empty;
                    }

                    _configWindow.ShouldSave();
                }

                ImGui.SameLine();

                if (ImGui.CollapsingHeader(
                        $"{character.CharacterName} {(character.Type != Configuration.CharacterType.NotManaged ? $"({character.Retainers.Count(x => x.Managed)} / {character.Retainers.Count})" : "")}###{character.LocalContentId}"))
                {
                    ImGui.Indent(_configWindow.MainIndentSize);

                    List<(Guid Id, string Name)> groups =
                        new List<(Guid Id, string Name)> { (Guid.Empty, "No Group (manually assign lists)") }
                            .Concat(_configuration.CharacterGroups.Select(x => (x.Id, x.Name)))
                            .ToList();

                    using (var tabBar = ImRaii.TabBar("CharOptions"))
                    {
                        if (tabBar)
                        {
                            if (character.Type != Configuration.CharacterType.NotManaged)
                                DrawVentureListTab(character, groups);

                            DrawCharacterRetainersTab(character);

                        }
                    }


                    ImGui.Unindent(_configWindow.MainIndentSize);
                }

                ImGui.PopID();
            }
        }
    }

    private void DrawVentureListTab(Configuration.CharacterConfiguration character, List<(Guid Id, string Name)> groups)
    {
        using var tab = ImRaii.TabItem("Venture Lists");
        if (!tab)
            return;

        int groupIndex = 0;
        if (character.Type == Configuration.CharacterType.PartOfCharacterGroup)
            groupIndex = groups.FindIndex(x => x.Id == character.CharacterGroupId);
        if (ImGui.Combo("Character Group", ref groupIndex, groups.Select(x => x.Name).ToArray(),
                groups.Count))
        {
            if (groupIndex == 0)
            {
                character.Type = Configuration.CharacterType.Standalone;
                character.CharacterGroupId = Guid.Empty;
            }
            else
            {
                character.Type = Configuration.CharacterType.PartOfCharacterGroup;
                character.CharacterGroupId = groups[groupIndex].Id;
            }

            _configWindow.ShouldSave();
        }

        ImGui.Separator();
        if (groupIndex == 0)
        {
            // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
            character.ItemListIds ??= new();
            _configWindow.DrawVentureListSelection(
                character.LocalContentId.ToString(CultureInfo.InvariantCulture),
                character.ItemListIds);
        }
        else
        {
            ImGui.TextWrapped($"Retainers will participate in the following lists:");
            ImGui.Indent(_configWindow.MainIndentSize);

            var group = _configuration.CharacterGroups.Single(
                x => x.Id == groups[groupIndex].Id);
            var lists = group.ItemListIds
                .Where(listId => listId != Guid.Empty)
                .Select(listId => _configuration.ItemLists.SingleOrDefault(x => x.Id == listId))
                .Where(list => list != null)
                .Cast<Configuration.ItemList>()
                .ToList();
            if (lists.Count > 0)
            {
                foreach (var list in lists)
                    ImGui.BulletText($"{list.Name}");
            }
            else
                ImGui.TextColored(ImGuiColors.DalamudRed, "(None)");

            ImGui.Unindent(_configWindow.MainIndentSize);
            ImGui.Spacing();
        }
    }

    private void DrawCharacterRetainersTab(Configuration.CharacterConfiguration character)
    {
        using var tab = ImRaii.TabItem("Retainers");
        if (!tab)
            return;

        foreach (var retainer in character.Retainers.Where(x => x.Job > 0)
                     .OrderBy(x => x.DisplayOrder)
                     .ThenBy(x => x.RetainerContentId))
        {
            ImGui.BeginDisabled(retainer is { Managed: false, Level: < ConfigWindow.MinLevel });

            bool managed = retainer.Managed;

            IDalamudTextureWrap? icon = _iconCache.GetIcon(62000 + retainer.Job);
            if (icon != null)
            {
                ImGui.Image(icon.ImGuiHandle, new Vector2(ImGui.GetFrameHeight()));
                ImGui.SameLine(0, ImGui.GetStyle().FramePadding.X);
            }

            if (ImGui.Checkbox(
                    $"{retainer.Name}###Retainer{retainer.Name}{retainer.RetainerContentId}",
                    ref managed))
            {
                retainer.Managed = managed;
                _configWindow.ShouldSave();
            }

            ImGui.EndDisabled();
        }
    }
}
