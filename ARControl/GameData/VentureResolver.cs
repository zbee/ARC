using System.Linq;
using Dalamud.Plugin.Services;

namespace ARControl.GameData;

internal sealed class VentureResolver
{
    private readonly GameCache _gameCache;
    private readonly IPluginLog _pluginLog;

    public VentureResolver(GameCache gameCache, IPluginLog pluginLog)
    {
        _gameCache = gameCache;
        _pluginLog = pluginLog;
    }

    public (Venture?, VentureReward?) ResolveVenture(Configuration.CharacterConfiguration character,
        Configuration.RetainerConfiguration retainer, uint itemId)
    {
        var venture = _gameCache.Ventures
            .Where(x => retainer.Level >= x.Level)
            .FirstOrDefault(x => x.ItemId == itemId && x.MatchesJob(retainer.Job));
        if (venture == null)
        {
            _pluginLog.Information($"No applicable venture found for itemId {itemId}");
            return (null, null);
        }

        var itemToGather = _gameCache.ItemsToGather.FirstOrDefault(x => x.ItemId == itemId);
        if (itemToGather != null && !character.GatheredItems.Contains(itemToGather.GatheredItemId))
        {
            _pluginLog.Information($"Character hasn't gathered {venture.Name} yet");
            return (null, null);
        }

        _pluginLog.Information(
            $"Found venture {venture.Name}, row = {venture.RowId}, checking if we have high enough stats");
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
