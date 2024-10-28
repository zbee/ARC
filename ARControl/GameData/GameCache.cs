using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.GeneratedSheets;

namespace ARControl.GameData;

internal sealed class GameCache
{
    public GameCache(IDataManager dataManager)
    {
        Jobs = dataManager.GetExcelSheet<ClassJob>()!.ToDictionary(x => x.RowId, x => x.Abbreviation.ToString());
        Ventures = dataManager.GetExcelSheet<RetainerTask>()!
            .Where(x => x.RowId > 0 && !x.IsRandom && x.Task != 0)
            .Select(x => new Venture(dataManager, x))
            .ToList()
            .AsReadOnly();
        ItemsToGather = dataManager.GetExcelSheet<GatheringItem>()!
            .Where(x => x.RowId > 0 && x.RowId < 10_000 && x.Item != 0 && x.Quest.Row == 0)
            .Where(x => Ventures.Any(y => y.ItemId == x.Item))
            .Select(x => new ItemToGather(dataManager, x))
            .OrderBy(x => x.Name)
            .ToList()
            .AsReadOnly();
        FolkloreBooks = dataManager.GetExcelSheet<GatheringSubCategory>()!
            .Where(x => x.RowId > 0)
            .Where(x => x.Item.Row != 0)
            .Select(x => new
            {
                x.RowId,
                ItemId = x.Item.Row,
                ItemName = x.Item.Value!.Name.ToString()
            })
            .GroupBy(x => (x.ItemId, x.ItemName))
            .Select(x =>
                new FolkloreBook
                {
                    ItemId = x.Key.ItemId,
                    Name = x.Key.ItemName,
                    GatheringSubCategories = x.Select(y => (ushort)y.RowId).ToList(),
                })
            .ToDictionary(x => x.ItemId, x => x);

        var gatheringNodes = dataManager.GetExcelSheet<GatheringPointBase>()!
            .Where(x => x.RowId > 0 && x.GatheringType.Row <= 3)
            .Select(x =>
                new
                {
                    GatheringPointBaseId = x.RowId,
                    GatheringPoint =
                        dataManager.GetExcelSheet<GatheringPoint>()!.FirstOrDefault(y =>
                            y.GatheringPointBase.Row == x.RowId),
                    Items = x.Item.Where(y => y > 0).ToList()
                })
            .Where(x => x.GatheringPoint != null)
            .Select(x =>
                new
                {
                    x.GatheringPointBaseId,
                    CategoryId = (ushort)x.GatheringPoint!.GatheringSubCategory.Row,
                    x.Items,
                })
            .ToList();
        var itemsWithoutTomes = gatheringNodes
            .Where(x => !FolkloreBooks.Values.Any(y => y.GatheringSubCategories.Contains(x.CategoryId)))
            .SelectMany(x => x.Items)
            .ToList();
        var itemsWithTomes = gatheringNodes
            .SelectMany(x => x.Items
                .Where(y => !itemsWithoutTomes.Contains(y))
                .Select(
                    y =>
                        new
                        {
                            x.CategoryId,
                            ItemId = (uint)y
                        }))
            .GroupBy(x => x.CategoryId)
            .ToDictionary(x => x.Key, x => x.Select(y => y.ItemId).ToList());
        foreach (var book in FolkloreBooks.Values)
        {
            book.TomeId = dataManager.GetExcelSheet<Item>()!.GetRow(book.ItemId)!.ItemAction.Value!.Data[0];
            foreach (var category in book.GatheringSubCategories)
            {
                if (itemsWithTomes.TryGetValue(category, out var itemsInCategory))
                    book.GatheringItemIds.AddRange(itemsInCategory);
            }
        }
    }

    public IReadOnlyDictionary<uint, string> Jobs { get; }
    public IReadOnlyList<Venture> Ventures { get; }
    public IReadOnlyList<ItemToGather> ItemsToGather { get; }
    public Dictionary<uint, FolkloreBook> FolkloreBooks { get; }
}
