using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace ARControl.GameData;

internal sealed class GameCache
{
    public GameCache(IDataManager dataManager)
    {
        Jobs = dataManager.GetExcelSheet<ClassJob>().ToDictionary(x => x.RowId, x => x.Abbreviation.ToString());
        Ventures = dataManager.GetExcelSheet<RetainerTask>()
            .Where(x => x.RowId > 0 && !x.IsRandom && x.Task.RowId != 0)
            .Select(x => new Venture(dataManager, x))
            .ToList()
            .AsReadOnly();
        ItemsToGather = dataManager.GetExcelSheet<GatheringItem>()
            .Where(x => x.RowId > 0 && x.RowId < 10_000 && x.Item.RowId != 0)
            .Where(x => Ventures.Any(y => y.ItemId == x.Item.RowId))
            .Select(x => new ItemToGather(x))
            .OrderBy(x => x.Name)
            .ToList()
            .AsReadOnly();
        FolkloreBooks = dataManager.GetExcelSheet<GatheringSubCategory>()
            .Where(x => x.RowId > 0)
            .Where(x => x.Item.RowId != 0)
            .Select(x => new
            {
                x.RowId,
                ItemId = x.Item.RowId,
                ItemName = x.Item.Value.Name.ToString()
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

        var gatheringNodes = dataManager.GetExcelSheet<GatheringPointBase>()
            .Where(x => x.RowId > 0 && x.GatheringType.RowId <= 3)
            .Select(x =>
                new
                {
                    GatheringPointBaseId = x.RowId,
                    GatheringPoint =
                        dataManager.GetExcelSheet<GatheringPoint>().Cast<GatheringPoint?>().FirstOrDefault(y =>
                            y!.Value.GatheringPointBase.RowId == x.RowId),
                    Items = x.Item.Where(y => y.RowId > 0).ToList()
                })
            .Where(x => x.GatheringPoint != null)
            .Select(x =>
                new
                {
                    x.GatheringPointBaseId,
                    CategoryId = (ushort)x.GatheringPoint!.Value.GatheringSubCategory.RowId,
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
                            ItemId = y.RowId
                        }))
            .GroupBy(x => x.CategoryId)
            .ToDictionary(x => x.Key, x => x.Select(y => y.ItemId).ToList());
        foreach (var book in FolkloreBooks.Values)
        {
            book.TomeId = dataManager.GetExcelSheet<Item>().GetRow(book.ItemId).ItemAction.Value.Data[0];
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
