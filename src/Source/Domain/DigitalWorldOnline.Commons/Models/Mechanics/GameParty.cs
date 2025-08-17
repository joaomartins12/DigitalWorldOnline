using System;
using System.Collections.Generic;
using System.Linq;
using DigitalWorldOnline.Commons.Enums.Party;
using DigitalWorldOnline.Commons.Models.Character;

namespace DigitalWorldOnline.Commons.Models.Mechanics
{
    public class GameParty
    {
        // Ajusta se precisares de outro limite
        public const int MaxSize = 6;

        private Dictionary<byte, CharacterModel> _members;

        public int Id { get; private set; }
        public PartyLootShareTypeEnum LootType { get; private set; }
        public PartyLootShareRarityEnum LootFilter { get; private set; }
        public int LeaderSlot { get; private set; }
        public long LeaderId { get; private set; }
        public DateTime CreateDate { get; }

        public Dictionary<byte, CharacterModel> Members
        {
            get => _members.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value);
            private set => _members = value;
        }

        public KeyValuePair<byte, CharacterModel> this[long memberId] =>
            Members.First(x => x.Value.Id == memberId);

        public KeyValuePair<byte, CharacterModel> this[string memberName] =>
            Members.First(x => string.Equals(x.Value.Name, memberName, StringComparison.OrdinalIgnoreCase));

        private GameParty(int id, CharacterModel leader, CharacterModel member)
        {
            Id = id;
            CreateDate = DateTime.Now;
            LootType = PartyLootShareTypeEnum.Normal;
            LootFilter = PartyLootShareRarityEnum.Lv1;
            LeaderSlot = 0;
            LeaderId = leader.Id;

            Members = new()
            {
                { 0, leader },
                { 1, member }
            };
        }

        public static GameParty Create(int id, CharacterModel leader, CharacterModel member)
            => new GameParty(id, leader, member);

        public void AddMember(CharacterModel member)
        {
            if (_members.Count >= MaxSize)
                throw new InvalidOperationException($"Party {Id} is full (Max={MaxSize}).");

            if (_members.Values.Any(m => m.Id == member.Id))
                throw new InvalidOperationException($"TamerId {member.Id} already in party {Id}.");

            // menor slot livre: 0..MaxSize-1
            byte newKey = FindFirstFreeSlot();
            _members.Add(newKey, member);
        }

        public void ChangeLeader(int newLeaderSlot)
        {
            if (newLeaderSlot < 0 || newLeaderSlot > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(newLeaderSlot));

            var key = (byte)newLeaderSlot;
            if (!_members.ContainsKey(key))
                throw new KeyNotFoundException($"Slot {newLeaderSlot} not found in party {Id}.");

            LeaderSlot = newLeaderSlot;
            LeaderId = _members[key].Id;
        }

        public void UpdateMember(KeyValuePair<byte, CharacterModel> member, CharacterModel newData)
        {
            if (_members.ContainsKey(member.Key))
                _members[member.Key] = newData;
        }

        public void RemoveMember(byte memberSlot)
        {
            if (!_members.ContainsKey(memberSlot))
                return;

            bool leaderWasRemoved = (_members[memberSlot].Id == LeaderId);

            _members.Remove(memberSlot);
            ReorderMembers();

            // Se o líder saiu, garante que LeaderSlot/LeaderId continuam coerentes
            if (leaderWasRemoved && _members.Count > 0)
            {
                // por padrão, promove o slot 0 (primeiro após compactação)
                LeaderSlot = 0;
                LeaderId = _members[(byte)LeaderSlot].Id;
            }
        }

        private void ReorderMembers()
        {
            // Reindexa 0..n-1 preservando a ordem por slot atual
            var ordered = _members.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();

            _members.Clear();

            for (byte i = 0; i < ordered.Count; i++)
            {
                var val = ordered[i];
                _members[i] = val;
                if (val.Id == LeaderId)
                    LeaderSlot = i;
            }
        }

        private byte FindFirstFreeSlot()
        {
            // 0..MaxSize-1
            for (byte i = 0; i < MaxSize; i++)
            {
                if (!_members.ContainsKey(i))
                    return i;
            }
            // fallback (não deveria ocorrer por causa do MaxSize check)
            var max = _members.Keys.Max();
            return (byte)(max + 1);
        }

        public void ChangeLootType(PartyLootShareTypeEnum lootType, PartyLootShareRarityEnum rareType)
        {
            LootType = lootType;
            LootFilter = rareType;
        }

        public List<long> GetMembersIdList() => _members.Values.Select(x => x.Id).ToList();

        // Helpers opcionais (não quebram API existente)
        public bool TryGetMemberById(long id, out KeyValuePair<byte, CharacterModel> entry)
        {
            foreach (var kv in _members)
            {
                if (kv.Value.Id == id)
                {
                    entry = kv;
                    return true;
                }
            }
            entry = default;
            return false;
        }

        public bool TryGetMemberByName(string name, out KeyValuePair<byte, CharacterModel> entry)
        {
            foreach (var kv in _members)
            {
                if (string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    entry = kv;
                    return true;
                }
            }
            entry = default;
            return false;
        }

        public object Clone() => MemberwiseClone();
    }
}
