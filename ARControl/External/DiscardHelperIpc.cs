using System.Collections.Generic;
using System.Collections.Immutable;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace ARControl.External;

internal sealed class DiscardHelperIpc
{
    private readonly ICallGateSubscriber<IReadOnlySet<uint>> _itemsToDiscard;

    public DiscardHelperIpc(IDalamudPluginInterface pluginInterface)
    {
        _itemsToDiscard = pluginInterface.GetIpcSubscriber<IReadOnlySet<uint>>("ARDiscard.GetItemsToDiscard");
    }

    public IReadOnlySet<uint> GetItemsToDiscard()
    {
        try
        {
            return _itemsToDiscard.InvokeFunc();
        }
        catch (IpcError)
        {
            // ignore
            return ImmutableHashSet<uint>.Empty;
        }
    }
}
