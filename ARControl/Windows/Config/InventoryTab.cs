using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using ARControl.External;
using ARControl.GameData;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using ImGuiNET;

namespace ARControl.Windows.Config;

internal sealed class InventoryTab : ITab
{
    private readonly Configuration _configuration;
    private readonly AllaganToolsIpc _allaganToolsIpc;
    private readonly GameCache _gameCache;
    private readonly IPluginLog _pluginLog;

    private List<TreeNode>? _listAsTrees;

    public InventoryTab(Configuration configuration, AllaganToolsIpc allaganToolsIpc, GameCache gameCache,
        IPluginLog pluginLog)
    {
        _configuration = configuration;
        _allaganToolsIpc = allaganToolsIpc;
        _gameCache = gameCache;
        _pluginLog = pluginLog;
    }

    public void Draw()
    {
        using var tab = ImRaii.TabItem("Inventory###TabInventory");
        if (!tab)
        {
            _listAsTrees = null;
            return;
        }

        if (_listAsTrees == null)
            RefreshInventory();

        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Redo, "Refresh"))
            RefreshInventory();

        ImGui.Separator();

        if (_listAsTrees == null || _listAsTrees.Count == 0)
        {
            ImGui.Text("No items in inventory. Do you have AllaganTools installed?");
            return;
        }

        foreach (var list in _configuration.ItemLists)
        {
            using var id = ImRaii.PushId($"List{list.Id}");
            if (ImGui.CollapsingHeader($"{list.Name} {list.GetIcon()}"))
            {
                using var indent = ImRaii.PushIndent();
                var rootNode = _listAsTrees.FirstOrDefault(x => x.Id == list.Id.ToString());
                if (rootNode == null || rootNode.Children.Count == 0)
                {
                    ImGui.Text("This list is empty.");
                    continue;
                }

                using var table = ImRaii.Table($"InventoryTable{list.Id}", 2, ImGuiTableFlags.NoSavedSettings);
                if (!table)
                    continue;

                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.NoHide);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 120 * ImGui.GetIO().FontGlobalScale);

                foreach (var child in rootNode.Children)
                    child.Draw();
            }
        }
    }

    private void RefreshInventory()
    {
        try
        {
            List<CharacterInventory> inventories = new();
            foreach (Configuration.CharacterConfiguration character in _configuration.Characters)
            {
                List<Guid> itemListIds = new();
                if (character.Type == Configuration.CharacterType.Standalone)
                {
                    itemListIds = character.ItemListIds;
                }
                else if (character.Type == Configuration.CharacterType.PartOfCharacterGroup)
                {
                    var group = _configuration.CharacterGroups.SingleOrDefault(x => x.Id == character.CharacterGroupId);
                    if (group != null)
                        itemListIds = group.ItemListIds;
                }

                var inventory = new CharacterInventory(character, itemListIds);

                var itemIdsOnLists = itemListIds.Where(listId => listId != Guid.Empty)
                    .Select(listId => _configuration.ItemLists.SingleOrDefault(x => x.Id == listId))
                    .Where(list => list != null)
                    .SelectMany(list => list!.Items.Select(x => x.ItemId))
                    .Distinct()
                    .ToHashSet();

                UpdateOwnedItems(character.LocalContentId, inventory.Items, itemIdsOnLists);
                foreach (var retainer in inventory.Retainers)
                    UpdateOwnedItems(retainer.Configuration.RetainerContentId, retainer.Items, itemIdsOnLists);

                inventories.Add(inventory);
            }

            List<TreeNode> listAsTrees = [];
            if (inventories.Count > 0)
            {
                foreach (var list in _configuration.ItemLists)
                {
                    TreeNode rootNode = new TreeNode(list.Id.ToString(), string.Empty, -1);
                    listAsTrees.Add(rootNode);

                    var relevantCharacters = inventories.Where(x => x.ItemListIds.Contains(list.Id)).ToList();
                    foreach (var item in list.Items)
                    {
                        var venture = _gameCache.Ventures.FirstOrDefault(x => x.ItemId == item.ItemId);
                        var total = relevantCharacters.Sum(x => x.CountItems(item.ItemId, list.CheckRetainerInventory));
                        TreeNode itemNode = rootNode.AddChild(item.InternalId.ToString(), venture?.Name ?? string.Empty,
                            total);

                        foreach (var character in relevantCharacters)
                        {
                            string characterName =
                                $"{character.Configuration.CharacterName} @ {character.Configuration.WorldName}";
                            long? stockQuantity = list.Type == Configuration.ListType.KeepStocked
                                ? item.RemainingQuantity
                                : null;
                            uint characterCount = character.CountItems(item.ItemId, list.CheckRetainerInventory);
                            if (characterCount == 0)
                                continue;
                            var characterNode = itemNode.AddChild(
                                character.Configuration.LocalContentId.ToString(CultureInfo.InvariantCulture),
                                characterName, characterCount, stockQuantity);

                            if (list.CheckRetainerInventory)
                            {
                                characterNode.AddChild("Self", "In Inventory",
                                    character.CountItems(item.ItemId, false));

                                foreach (var retainer in character.Retainers)
                                {
                                    uint retainerCount = retainer.CountItems(item.ItemId);
                                    if (retainerCount == 0)
                                        continue;
                                    characterNode.AddChild(
                                        retainer.Configuration.RetainerContentId.ToString(CultureInfo.InvariantCulture),
                                        retainer.Configuration.Name, retainerCount);
                                }
                            }
                        }
                    }
                }
            }

            _listAsTrees = listAsTrees;
        }
        catch (Exception e)
        {
            _listAsTrees = [];
            _pluginLog.Error(e, "Failed to load inventories via AllaganTools");
        }
    }

    private void UpdateOwnedItems(ulong localContentId, List<Item> items, HashSet<uint> itemIdsOnLists)
    {
        var ownedItems = _allaganToolsIpc.GetCharacterItems(localContentId);
        foreach (var ownedItem in ownedItems)
        {
            if (!itemIdsOnLists.Contains(ownedItem.ItemId))
                continue;

            items.Add(new Item(ownedItem.ItemId, ownedItem.Quantity));
        }
    }

    private sealed class CharacterInventory
    {
        public CharacterInventory(Configuration.CharacterConfiguration configuration, List<Guid> itemListIds)
        {
            Configuration = configuration;
            ItemListIds = itemListIds;
            Retainers = configuration.Retainers.Where(x => x is { Job: > 0, Managed: true })
                .OrderBy(x => x.DisplayOrder)
                .ThenBy(x => x.RetainerContentId)
                .Select(x => new RetainerInventory(x))
                .ToList();
        }

        public Configuration.CharacterConfiguration Configuration { get; }
        public List<Guid> ItemListIds { get; }
        public List<RetainerInventory> Retainers { get; }
        public List<Item> Items { get; } = [];

        public uint CountItems(uint itemId, bool checkRetainerInventory)
        {
            uint sum = (uint)Items.Where(x => x.ItemId == itemId).Sum(x => x.Quantity);
            if (checkRetainerInventory)
                sum += (uint)Retainers.Sum(x => x.CountItems(itemId));

            return sum;
        }
    }

    private sealed class RetainerInventory(Configuration.RetainerConfiguration configuration)
    {
        public Configuration.RetainerConfiguration Configuration { get; } = configuration;
        public List<Item> Items { get; } = [];

        public uint CountItems(uint itemId) => (uint)Items.Where(x => x.ItemId == itemId).Sum(x => x.Quantity);
    }

    private sealed record Item(uint ItemId, uint Quantity);

    private sealed record TreeNode(string Id, string Label, long Quantity, long? StockQuantity = null)
    {
        public List<TreeNode> Children { get; } = [];

        public TreeNode AddChild(string id, string label, long quantity, long? stockQuantity = null)
        {
            TreeNode child = new TreeNode(id, label, quantity, stockQuantity);
            Children.Add(child);
            return child;
        }

        public void Draw()
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (Children.Count > 0)
            {
                bool open = ImGui.TreeNodeEx(Label, ImGuiTreeNodeFlags.SpanFullWidth);

                ImGui.TableNextColumn();
                DrawCount();

                if (open)
                {
                    foreach (var child in Children)
                        child.Draw();

                    ImGui.TreePop();
                }
            }
            else
            {
                ImGui.TreeNodeEx(Label,
                    ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanFullWidth);

                ImGui.TableNextColumn();
                DrawCount();
            }
        }

        private void DrawCount()
        {
            if (StockQuantity != null)
            {
                using var color = ImRaii.PushColor(ImGuiCol.Text, Quantity >= StockQuantity.Value
                    ? ImGuiColors.HealerGreen
                    : ImGuiColors.DalamudRed);
                ImGui.TextUnformatted(string.Create(CultureInfo.CurrentCulture,
                    $"{Quantity:N0} / {StockQuantity.Value:N0}"));
            }
            else
                ImGui.TextUnformatted(Quantity.ToString("N0", CultureInfo.CurrentCulture));
        }
    }
}
