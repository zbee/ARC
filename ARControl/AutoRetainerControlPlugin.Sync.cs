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

            if (character.Ventures != offlineCharacterData.Ventures)
            {
                character.Ventures = offlineCharacterData.Ventures;
                save = true;
            }

            // migrate legacy retainers
            foreach (var legacyRetainer in character.Retainers.Where(x => x.RetainerContentId == 0))
            {
                var retainerData =
                    offlineCharacterData.RetainerData.SingleOrDefault(x => legacyRetainer.Name == x.Name);
                if (retainerData != null)
                {
                    _pluginLog.Information(
                        $"Assigning contentId {retainerData.RetainerID} to retainer {retainerData.Name}");
                    legacyRetainer.RetainerContentId = retainerData.RetainerID;
                    save = true;
                }
            }

            var retainersWithoutContentId = character.Retainers.Where(c => c.RetainerContentId == 0).ToList();
            if (retainersWithoutContentId.Count > 0)
            {
                foreach (var retainer in retainersWithoutContentId)
                {
                    _pluginLog.Warning($"Removing retainer {retainer.Name} without contentId");
                    character.Retainers.Remove(retainer);
                }

                save = true;
            }

            List<ulong> unknownRetainerIds = offlineCharacterData.RetainerData.Select(x => x.RetainerID).Where(x => x != 0).ToList();
            foreach (var retainerData in offlineCharacterData.RetainerData)
            {
                unknownRetainerIds.Remove(retainerData.RetainerID);

                var retainer = character.Retainers.SingleOrDefault(x => x.RetainerContentId == retainerData.RetainerID);
                if (retainer == null)
                {
                    retainer = new Configuration.RetainerConfiguration
                    {
                        RetainerContentId = retainerData.RetainerID,
                        Name = retainerData.Name,
                        Managed = false,
                    };

                    save = true;
                    character.Retainers.Add(retainer);
                }

                if (retainer.Name != retainerData.Name)
                {
                    retainer.Name = retainerData.Name;
                    save = true;
                }

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

            if (unknownRetainerIds.Count > 0)
            {
                foreach (var retainerId in unknownRetainerIds)
                {
                    _pluginLog.Warning($"Removing unknown retainer with contentId {retainerId}");
                    character.Retainers.RemoveAll(c => c.RetainerContentId == retainerId);
                }

                save = true;
            }
        }

        if (save)
            _pluginInterface.SavePluginConfig(_configuration);
    }
}
