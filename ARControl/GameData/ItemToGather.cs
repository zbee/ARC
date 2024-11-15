using Lumina.Excel.Sheets;

namespace ARControl.GameData;

internal sealed class ItemToGather
{
    public ItemToGather(GatheringItem item)
    {
        GatheredItemId = item.RowId;

        var itemRef = item.Item.GetValueOrDefault<Item>()!.Value;
        ItemId = itemRef.RowId;
        Name = itemRef.Name.ToString();
    }


    public uint GatheredItemId { get; }
    public uint ItemId { get; }
    public string Name { get; }
}
