using System;
using System.Linq;
using System.Threading.Tasks;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Application.Separar.Queries;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.Character;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Packets.MapServer;
using DigitalWorldOnline.Commons.Utils;
using DigitalWorldOnline.Game.Managers;
using DigitalWorldOnline.GameHost;
using MediatR;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class PartyMemberLeavePacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.PartyMemberLeave;

        private const string GameServerAddress = "GameServer:Address";
        private const string GamerServerPublic = "GameServer:PublicAddress";
        private const string GameServerPort = "GameServer:Port";

        private readonly PartyManager _partyManager;
        private readonly MapServer _mapServer;
        private readonly DungeonsServer _dungeonServer;
        private readonly ILogger _logger;
        private readonly ISender _sender;
        private readonly IConfiguration _configuration;

        public PartyMemberLeavePacketProcessor(
            PartyManager partyManager,
            MapServer mapServer,
            ILogger logger,
            ISender sender,
            IConfiguration configuration,
            DungeonsServer dungeonServer)
        {
            _partyManager = partyManager;
            _mapServer = mapServer;
            _logger = logger;
            _sender = sender;
            _configuration = configuration;
            _dungeonServer = dungeonServer;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            try
            {
                var packet = new GamePacketReader(packetData);
                var targetName = packet.ReadString(); // opcional, útil para logs/UI

                var party = _partyManager.FindParty(client.TamerId);
                if (party == null)
                {
                    _logger.Error("LeaveParty: TamerId={TamerId} tried to leave but is not in a party.", client.TamerId);
                    return;
                }

                // indexer por tamerId; .Key é o slot (byte)
                var pair = party[client.TamerId];
                byte leaveTargetKey = pair.Key;

                var membersIds = party.GetMembersIdList(); // snapshot antes de alterações
                var leavePayload = new PartyMemberLeavePacket(leaveTargetKey).Serialize();

                // Caso 1: Líder sai e party tem 3+ membros → promover novo líder
                if (party.LeaderSlot == leaveTargetKey && party.Members.Count > 2)
                {
                    _mapServer.BroadcastForTargetTamers(membersIds, leavePayload);
                    _dungeonServer.BroadcastForTargetTamers(membersIds, leavePayload);

                    // remove primeiro
                    party.RemoveMember(leaveTargetKey);

                    // escolhe novo líder (aqui mantive a aleatória; se preferires, usa o menor slot disponível)
                    var rndIndex = new Random().Next(party.Members.Count);
                    var newLeaderSlot = party.Members.ElementAt(rndIndex).Key;
                    party.ChangeLeader(newLeaderSlot);

                    var leaderChangedPayload = new PartyLeaderChangedPacket(newLeaderSlot).Serialize();
                    _mapServer.BroadcastForTargetTamers(membersIds, leaderChangedPayload);
                    _dungeonServer.BroadcastForTargetTamers(membersIds, leaderChangedPayload);

                    // se o jogador que saiu estava em dungeon, teleporta-o para fora
                    var leavingClientInDungeon = _dungeonServer.FindClientByTamerId(client.TamerId);
                    if (leavingClientInDungeon != null)
                        await TeleportOutOfDungeon(leavingClientInDungeon);

                    _logger.Information("LeaveParty: Leader TamerId={TamerId} left PartyId={PartyId}; new leader slot={Slot}",
                        client.TamerId, party.Id, newLeaderSlot);

                    return;
                }

                // Caso 2: Party ficará com <= 2 membros após a saída → dissolver
                if (party.Members.Count <= 2)
                {
                    _mapServer.BroadcastForTargetTamers(membersIds, leavePayload);
                    _dungeonServer.BroadcastForTargetTamers(membersIds, leavePayload);

                    // remove o jogador que saiu
                    party.RemoveMember(leaveTargetKey);

                    // dissolvendo: tirar QUALQUER membro restante da dungeon
                    var remainingMembers = party.Members.Values.ToList();
                    foreach (var m in remainingMembers)
                    {
                        var dc = _dungeonServer.FindClientByTamerId(m.Id);
                        if (dc != null)
                            await TeleportOutOfDungeon(dc);
                    }

                    _partyManager.RemoveParty(party.Id);

                    // se o que saiu estava em dungeon, teleporta também
                    var leavingClientInDungeon = _dungeonServer.FindClientByTamerId(client.TamerId);
                    if (leavingClientInDungeon != null)
                        await TeleportOutOfDungeon(leavingClientInDungeon);

                    _logger.Information("LeaveParty: PartyId={PartyId} disbanded after TamerId={TamerId} left.",
                        party.Id, client.TamerId);
                    return;
                }

                // Caso 3: Membro comum sai (party continua existindo com 2+ membros)
                {
                    _mapServer.BroadcastForTargetTamers(membersIds, leavePayload);
                    _dungeonServer.BroadcastForTargetTamers(membersIds, leavePayload);

                    party.RemoveMember(leaveTargetKey);

                    // Se o que saiu estava em dungeon, teleporta-o para fora
                    var dc = _dungeonServer.FindClientByTamerId(client.TamerId);
                    if (dc != null)
                        await TeleportOutOfDungeon(dc);

                    _logger.Information("LeaveParty: TamerId={TamerId} left PartyId={PartyId}.", client.TamerId, party.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "LeaveParty: exception processing leave for TamerId={TamerId}", client.TamerId);
            }
        }

        private GameClient? FindClient(long tamerId)
        {
            var c = _mapServer.FindClientByTamerId(tamerId);
            return c ?? _dungeonServer.FindClientByTamerId(tamerId);
        }

        private async Task TeleportOutOfDungeon(GameClient client)
        {
            try
            {
                var mapGroupId = UtilitiesFunctions.MapGroup(client.Tamer.Location.MapId);

                var mapConfig = await _sender.Send(new GameMapConfigByMapIdQuery(client.Tamer.Location.MapId));
                var waypoints = await _sender.Send(new MapRegionListAssetsByMapIdQuery(mapGroupId));

                if (mapConfig == null || waypoints == null || !waypoints.Regions.Any())
                {
                    client.Send(new SystemMessagePacket($"Map information not found for map Id {mapGroupId}."));
                    _logger.Warning("TeleportOutOfDungeon: map info not found for mapId={Map} (tamerId={TamerId})",
                        mapGroupId, client.TamerId);
                    return;
                }

                var destination = waypoints.Regions.FirstOrDefault(x => x.Index == mapConfig.MapRegionindex);
                if (destination == null)
                {
                    client.Send(new SystemMessagePacket($"Spawn point not found for map Id {mapGroupId}."));
                    _logger.Warning("TeleportOutOfDungeon: spawn not found for mapId={Map}, index={Index} (tamerId={TamerId})",
                        mapGroupId, mapConfig.MapRegionindex, client.TamerId);
                    return;
                }

                _dungeonServer.RemoveClient(client);

                client.Tamer.NewLocation(mapGroupId, destination.X, destination.Y);
                await _sender.Send(new UpdateCharacterLocationCommand(client.Tamer.Location));

                client.Tamer.Partner.NewLocation(mapGroupId, destination.X, destination.Y);
                await _sender.Send(new UpdateDigimonLocationCommand(client.Tamer.Partner.Location));

                client.Tamer.UpdateState(CharacterStateEnum.Loading);
                await _sender.Send(new UpdateCharacterStateCommand(client.TamerId, CharacterStateEnum.Loading));

                client.SetGameQuit(false);

                client.Send(new MapSwapPacket(
                    _configuration[GamerServerPublic] ?? _configuration[GameServerAddress],
                    _configuration[GameServerPort],
                    client.Tamer.Location.MapId,
                    client.Tamer.Location.X,
                    client.Tamer.Location.Y
                ));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "TeleportOutOfDungeon: error while moving TamerId={TamerId} out", client.TamerId);
            }
        }
    }
}
