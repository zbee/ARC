using System.Collections.Generic;
using System.Linq;

namespace ARControl;

partial class AutoRetainerControlPlugin
{
    private void Sync()
    {
        bool save = false;

        // FIXME This should have a way to get blacklisted character ids
        foreach (ulong registeredCharacterId in _autoRetainerApi.GetRegisteredCharacters())
        {
            _pluginLog.Verbose($"Sync for character {registeredCharacterId:X}");
            var offlineCharacterData = _autoRetainerApi.GetOfflineCharacterData(registeredCharacterId);
            if (offlineCharacterData.ExcludeRetainer)
                continue;

            var character = _configuration.Characters.SingleOrDefault(x => x.LocalContentId == registeredCharacterId);
            if (character == null)
            {
                character = new Configuration.CharacterConfiguration
                {
                    LocalContentId = registeredCharacterId,
                    CharacterName = offlineCharacterData.Name,
                    WorldName = offlineCharacterData.World,
                    Type = Configuration.CharacterType.NotManaged,
                };

                save = true;
                _configuration.Characters.Add(character);
            }

            if (character.GatheredItems != offlineCharacterData.UnlockedGatheringItems)
            {
                character.GatheredItems = offlineCharacterData.UnlockedGatheringItems;
                save = true;
            }

            List<string> seenRetainers = new();
            foreach (var retainerData in offlineCharacterData.RetainerData)
            {
                var retainer = character.Retainers.SingleOrDefault(x => x.Name == retainerData.Name);
                if (retainer == null)
                {
                    retainer = new Configuration.RetainerConfiguration
                    {
                        Name = retainerData.Name,
                        Managed = false,
                    };

                    save = true;
                    character.Retainers.Add(retainer);
                }

                seenRetainers.Add(retainer.Name);

                if (retainer.DisplayOrder != retainerData.DisplayOrder)
                {
                    retainer.DisplayOrder = retainerData.DisplayOrder;
                    save = true;
                }

                if (retainer.Level != retainerData.Level)
                {
                    retainer.Level = retainerData.Level;
                    save = true;
                }

                if (retainer.Job != retainerData.Job)
                {
                    retainer.Job = retainerData.Job;
                    save = true;
                }

                if (retainer.HasVenture != retainerData.HasVenture)
                {
                    retainer.HasVenture = retainerData.HasVenture;
                    save = true;
                }

                if (retainer.LastVenture != retainerData.VentureID)
                {
                    retainer.LastVenture = retainerData.VentureID;
                    save = true;
                }

                var additionalData =
                    _autoRetainerApi.GetAdditionalRetainerData(registeredCharacterId, retainerData.Name);
                if (retainer.ItemLevel != additionalData.Ilvl)
                {
                    retainer.ItemLevel = additionalData.Ilvl;
                    save = true;
                }

                if (retainer.Gathering != additionalData.Gathering)
                {
                    retainer.Gathering = additionalData.Gathering;
                    save = true;
                }

                if (retainer.Perception != additionalData.Perception)
                {
                    retainer.Perception = additionalData.Perception;
                    save = true;
                }
            }

            if (character.Retainers.RemoveAll(x => !seenRetainers.Contains(x.Name)) > 0)
                save = true;
        }

        if (save)
            _pluginInterface.SavePluginConfig(_configuration);
    }
}
