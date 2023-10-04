using Dalamud.Plugin.Services;
using Lumina.Excel.GeneratedSheets;

namespace ARControl.GameData;

internal sealed class ItemToGather
{
    public ItemToGather(IDataManager dataManager, GatheringItem item)
    {
        GatheredItemId = item.RowId;
        ItemId = item.Item;
        Name = dataManager.GetExcelSheet<Item>()!.GetRow((uint)item.Item)!.Name.ToString();
    }


    public uint GatheredItemId { get; }
    public int ItemId { get; }
    public string Name { get; }
}
