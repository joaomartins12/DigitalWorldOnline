using System;
using System.Linq;
using System.Threading.Tasks;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Game.Managers;
using DigitalWorldOnline.GameHost;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class PartyRequestResponsePacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.PartyRequestResponse;

        private readonly PartyManager _partyManager;
        private readonly MapServer _mapServer;
        private readonly DungeonsServer _dungeonServer;
        private readonly ILogger _logger;

        private const int InviteRejected = -1;

        public PartyRequestResponsePacketProcessor(
            PartyManager partyManager,
            MapServer mapServer,
            DungeonsServer dungeonServer,
            ILogger logger)
        {
            _partyManager = partyManager;
            _mapServer = mapServer;
            _dungeonServer = dungeonServer;
            _logger = logger;
        }

        public Task Process(GameClient client, byte[] packetData)
        {
            try
            {
                var packet = new GamePacketReader(packetData);
                var inviteResult = packet.ReadInt();
                var leaderNameRaw = packet.ReadString();
                var leaderName = leaderNameRaw?.Trim();

                if (string.IsNullOrWhiteSpace(leaderName))
                {
                    client.Send(new SystemMessagePacket("Invalid party leader name."));
                    _logger.Warning("PartyInviteResponse: empty leader name from TamerId={TamerId}", client.TamerId);
                    return Task.CompletedTask;
                }

                var leaderClient = FindClientByName(leaderName);
                if (leaderClient == null)
                {
                    _logger.Warning("PartyInviteResponse: leader '{Leader}' not found.", leaderName);
                    client.Send(new SystemMessagePacket($"Unable to find party leader with name {leaderName}."));
                    return Task.CompletedTask;
                }

                // Rejeitado
                if (inviteResult == InviteRejected)
                {
                    leaderClient.Send(new PartyRequestSentFailedPacket(PartyRequestFailedResultEnum.Rejected, client.Tamer.Name));
                    _logger.Verbose("PartyInviteResponse: TamerId={Client} refused invite from LeaderId={Leader}.",
                        client.TamerId, leaderClient.TamerId);
                    return Task.CompletedTask;
                }

                // Aceite
                var party = _partyManager.FindParty(leaderClient.TamerId);

                if (party == null)
                {
                    // criar party
                    party = _partyManager.CreateParty(leaderClient.Tamer, client.Tamer);
                    _logger.Verbose("PartyInviteResponse: LeaderId={Leader} created PartyId={Party} with MemberId={Member}.",
                        leaderClient.TamerId, party.Id, client.TamerId);

                    // se o líder está numa dungeon, propaga o id da party ao mapa da dungeon (mantive a tua lógica)
                    if (leaderClient.DungeonMap)
                    {
                        var targetMap = _dungeonServer.Maps.FirstOrDefault(x => x.DungeonId == leaderClient.TamerId);
                        targetMap?.SetId(party.Id);
                    }

                    // notificações
                    leaderClient.Send(new PartyCreatedPacket(party.Id, party.LootType));
                    leaderClient.Send(new PartyRequestSentFailedPacket(PartyRequestFailedResultEnum.Accept, client.Tamer.Name));
                    leaderClient.Send(new PartyMemberJoinPacket(party[client.TamerId], true));
                    leaderClient.Send(new PartyMemberInfoPacket(party[client.TamerId]));

                    client.Send(new PartyMemberListPacket(party, client.TamerId));
                }
                else
                {
                    // adicionar membro
                    party.AddMember(client.Tamer);
                    _logger.Verbose("PartyInviteResponse: MemberId={Member} joined PartyId={Party} (LeaderId={Leader}).",
                        client.TamerId, party.Id, leaderClient.TamerId);

                    // index do slot do novo membro (count-1 após Add)
                    client.Send(new PartyMemberListPacket(party, client.TamerId, (byte)(party.Members.Count - 1)));
                    leaderClient.Send(new PartyRequestSentFailedPacket(PartyRequestFailedResultEnum.Accept, client.Tamer.Name));

                    // avisar restantes membros (mapa/dungeon), exceto o próprio que entrou
                    BroadcastJoinToExistingMembers(party, client.Tamer.Id);
                }

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "PartyInviteResponse: exception processing response from TamerId={TamerId}", client.TamerId);
                return Task.CompletedTask;
            }
        }

        private GameClient? FindClientByName(string name)
        {
            var c = _mapServer.FindClientByTamerName(name);
            return c ?? _dungeonServer.FindClientByTamerName(name);
        }

        private void BroadcastJoinToExistingMembers(Commons.Models.Mechanics.GameParty party, long joinedTamerId)
        {
            // snapshot
            var memberPairs = party.Members.ToList();
            var joinEntry = party[joinedTamerId];

            foreach (var kv in memberPairs)
            {
                var member = kv.Value;
                if (member.Id == joinedTamerId) continue;

                var targetClient = _mapServer.FindClientByTamerId(member.Id)
                                  ?? _dungeonServer.FindClientByTamerId(member.Id);

                if (targetClient == null) continue;

                var isLeader = (member.Id == party.LeaderId);
                if (isLeader)
                {
                    targetClient.Send(new PartyMemberJoinPacket(joinEntry, true));
                    targetClient.Send(new PartyMemberInfoPacket(joinEntry, true));
                }
                else
                {
                    targetClient.Send(new PartyMemberJoinPacket(joinEntry));
                    targetClient.Send(new PartyMemberInfoPacket(joinEntry));
                }
            }
        }
    }
}
