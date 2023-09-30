using System;
using System.Linq;
using Dalamud.Logging;

namespace ARControl.GameData;

internal sealed class VentureResolver
{
    private readonly GameCache _gameCache;

    public VentureResolver(GameCache gameCache)
    {
        _gameCache = gameCache;
    }

    public (Venture?, VentureReward?) ResolveVenture(Configuration.CharacterConfiguration character,
        Configuration.RetainerConfiguration retainer, Configuration.QueuedItem queuedItem)
    {
        var venture = _gameCache.Ventures
            .Where(x => retainer.Level >= x.Level)
            .FirstOrDefault(x => x.ItemId == queuedItem.ItemId && x.MatchesJob(retainer.Job));
        if (venture == null)
        {
            PluginLog.Information($"No applicable venture found for itemId {queuedItem.ItemId}");
            return (null, null);
        }

        var itemToGather = _gameCache.ItemsToGather.FirstOrDefault(x => x.ItemId == queuedItem.ItemId);
        if (itemToGather != null && !character.GatheredItems.Contains(itemToGather.GatheredItemId))
        {
            PluginLog.Information($"Character hasn't gathered {venture.Name} yet");
            return (null, null);
        }

        PluginLog.Information(
            $"Found venture {venture.Name}, row = {venture.RowId}, checking if it is suitable");
        VentureReward? reward = null;
        if (venture.CategoryName is "MIN" or "BTN")
        {
            if (retainer.Gathering >= venture.RequiredGathering)
                reward = venture.Rewards.Last(
                    x => retainer.Perception >= x.PerceptionMinerBotanist);
        }
        else if (venture.CategoryName == "FSH")
        {
            if (retainer.Gathering >= venture.RequiredGathering)
                reward = venture.Rewards.Last(
                    x => retainer.Perception >= x.PerceptionFisher);
        }
        else
        {
            if (retainer.ItemLevel >= venture.ItemLevelCombat)
                reward = venture.Rewards.Last(
                    x => retainer.ItemLevel >= x.ItemLevelCombat);
        }

        return (venture, reward);
    }
}
