using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace ARControl.GameData;

internal sealed class FolkloreBook
{
    public required uint ItemId { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<ushort> GatheringSubCategories { get; init; }
    public List<uint> GatheringItemIds { get; } = [];
    public ushort TomeId { get; set; }

    public unsafe bool IsUnlocked()
    {
        return PlayerState.Instance()->IsFolkloreBookUnlocked(TomeId);
    }
}
