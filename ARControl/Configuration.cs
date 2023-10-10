using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Interface;

namespace ARControl;

internal sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; }

    public List<CharacterConfiguration> Characters { get; set; } = new();
    public List<ItemList> ItemLists { get; set; } = new();
    public List<CharacterGroup> CharacterGroups { get; set; } = new();

    public sealed class ItemList
    {
        public required Guid Id { get; set; }
        public required string Name { get; set; }
        public required ListType Type { get; set; } = ListType.CollectOneTime;
        public required ListPriority Priority { get; set; } = ListPriority.InOrder;
        public List<QueuedItem> Items { get; set; } = new();
    }

    public enum ListType
    {
        CollectOneTime,
        KeepStocked,
    }

    public enum ListPriority
    {
        InOrder,
        Balanced,
    }

    public sealed class QueuedItem
    {
        public required uint ItemId { get; set; }
        public required int RemainingQuantity { get; set; }
    }

    public sealed class CharacterGroup
    {
        public required Guid Id { get; set; }
        public required string Name { get; set; }
        public required FontAwesomeIcon Icon { get; set; }
        public List<Guid> ItemListIds { get; set; } = new();
    }

    public sealed class CharacterConfiguration
    {
        public required ulong LocalContentId { get; set; }
        public required string CharacterName { get; set; }
        public required string WorldName { get; set; }

        public CharacterType Type { get; set; } = CharacterType.NotManaged;
        public Guid CharacterGroupId { get; set; }
        public List<Guid> ItemListIds { get; set; } = new();

        public List<RetainerConfiguration> Retainers { get; set; } = new();
        public HashSet<uint> GatheredItems { get; set; } = new();

        public override string ToString() => $"{CharacterName} @ {WorldName}";
    }

    public enum CharacterType
    {
        NotManaged,

        /// <summary>
        /// The character's item list(s) are manually selected.
        /// </summary>
        Standalone,

        /// <summary>
        /// All item lists are managed through the character group.
        /// </summary>
        PartOfCharacterGroup
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
