using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ARControl.External;

internal sealed class AllaganToolsIpc
{
    private readonly IPluginLog _pluginLog;

    private static readonly uint[] RetainerInventoryTypes = new[]
        {
            InventoryType.RetainerPage1,
            InventoryType.RetainerPage2,
            InventoryType.RetainerPage3,
            InventoryType.RetainerPage4,
            InventoryType.RetainerPage5,
            InventoryType.RetainerPage6,
            InventoryType.RetainerPage7,
            InventoryType.RetainerCrystals,
        }
        .Select(x => (uint)x).ToArray();

    private readonly ICallGateSubscriber<ulong,HashSet<ulong[]>> _getClownItems;
    private readonly ICallGateSubscriber<uint, bool, uint[], uint> _itemCountOwned;

    public AllaganToolsIpc(IDalamudPluginInterface pluginInterface, IPluginLog pluginLog)
    {
        _pluginLog = pluginLog;
        _getClownItems = pluginInterface.GetIpcSubscriber<ulong, HashSet<ulong[]>>("AllaganTools.GetCharacterItems");
        _itemCountOwned = pluginInterface.GetIpcSubscriber<uint, bool, uint[], uint>("AllaganTools.ItemCountOwned");
    }

    public List<(uint ItemId, uint Quantity)> GetCharacterItems(ulong contentId)
    {
        try
        {
            HashSet<ulong[]> items = _getClownItems.InvokeFunc(contentId);
            _pluginLog.Information($"CID: {contentId}, Items: {items.Count}");

            return items.Select(x => (ItemId: (uint)x[2], Quantity: (uint)x[3]))
                .GroupBy(x => x.ItemId)
                .Select(x => (x.Key, (uint)x.Sum(y => y.Quantity)))
                .ToList();
        }
        catch (TargetInvocationException e)
        {
            _pluginLog.Information(e, $"Unable to retrieve items for character {contentId}");
            return [];
        }
        catch (IpcError)
        {
            _pluginLog.Warning("Could not query allagantools for character items");
            return [];
        }
    }

    public uint GetRetainerItemCount(uint itemId)
    {
        try
        {
            uint itemCount = _itemCountOwned.InvokeFunc(itemId, true, RetainerInventoryTypes);
            _pluginLog.Verbose($"Found {itemCount} items in retainer inventories for itemId {itemId}");
            return itemCount;
        }
        catch (IpcError)
        {
            _pluginLog.Warning("Could not query allagantools for retainer inventory counts");
            return 0;
        }
    }
}
