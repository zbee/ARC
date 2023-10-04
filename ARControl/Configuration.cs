using System.Collections.Generic;
using Dalamud.Configuration;

namespace ARControl;

internal sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public List<QueuedItem> QueuedItems { get; set; } = new();
    public List<CharacterConfiguration> Characters { get; set; } = new();

    public sealed class QueuedItem
    {
        public required uint ItemId { get; set; }
        public required int RemainingQuantity { get; set; }
    }

    public sealed class CharacterConfiguration
    {
        public required ulong LocalContentId { get; set; }
        public required string CharacterName { get; set; }
        public required string WorldName { get; set; }
        public required bool Managed { get; set; }

        public List<RetainerConfiguration> Retainers { get; set; } = new();
        public HashSet<uint> GatheredItems { get; set; } = new();

        public override string ToString() => $"{CharacterName} @ {WorldName}";
    }

    public sealed class RetainerConfiguration
    {
        public required string Name { get; set; }
        public required bool Managed { get; set; }
        public int DisplayOrder { get; set; }
        public int Level { get; set; }
        public uint Job { get; set; }
        public uint LastVenture { get; set; }
        public int ItemLevel { get; set; }
        public int Gathering { get; set; }
        public int Perception { get; set; }
    }
}
