using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DigitalWorldOnline.Commons.Models.Character;
using DigitalWorldOnline.Commons.Models.Mechanics;

namespace DigitalWorldOnline.Game.Managers
{
    public class PartyManager
    {
        private int _partyId;
        private readonly object _sync = new();

        private readonly List<GameParty> _parties = new();
        public IReadOnlyList<GameParty> Parties
        {
            get { lock (_sync) return _parties.ToList(); }
        }

        public GameParty CreateParty(CharacterModel leader, CharacterModel member)
        {
            var id = Interlocked.Increment(ref _partyId);
            var party = GameParty.Create(id, leader, member);

            lock (_sync)
            {
                _parties.Add(party);
            }

            return party;
        }

        public GameParty? FindParty(long leaderOrMemberId)
        {
            lock (_sync)
            {
                return _parties.FirstOrDefault(
                    p => p.Members.Values.Any(m => m.Id == leaderOrMemberId)
                );
            }
        }

        public void RemovePartyIfLastMember(long partyId)
        {
            lock (_sync)
            {
                var party = _parties.FirstOrDefault(p => p.Id == partyId);
                if (party == null) return;

                // Usa <= 1 para também cobrir party “vazia” por algum motivo
                if (party.Members.Count <= 1)
                {
                    _parties.RemoveAll(p => p.Id == partyId);
                }
            }
        }

        public bool IsMemberInParty(long leaderOrMemberId, long tamerId)
        {
            lock (_sync)
            {
                var party = _parties.FirstOrDefault(
                    p => p.Members.Values.Any(m => m.Id == leaderOrMemberId)
                );
                if (party == null) return false;

                return party.Members.Values.Any(x => x.Id == tamerId);
            }
        }

        public bool RemoveParty(long partyId)
        {
            lock (_sync)
            {
                return _parties.RemoveAll(x => x.Id == partyId) > 0;
            }
        }
    }
}
