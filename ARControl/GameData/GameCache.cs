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
    }

    public IReadOnlyDictionary<uint, string> Jobs { get; }
    public IReadOnlyList<Venture> Ventures { get; }
    public IReadOnlyList<ItemToGather> ItemsToGather { get; }
}
