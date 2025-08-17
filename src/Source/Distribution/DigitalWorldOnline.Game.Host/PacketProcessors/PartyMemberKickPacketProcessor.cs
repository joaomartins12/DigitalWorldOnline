using System;
using System.Linq;
using System.Threading.Tasks;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Application.Separar.Queries;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.Character;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Mechanics;
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
    public class PartyMemberKickPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.PartyMemberKick;

        private const string GameServerAddress = "GameServer:Address";
        private const string GamerServerPublic = "GameServer:PublicAddress";
        private const string GameServerPort = "GameServer:Port";

        private readonly PartyManager _partyManager;
        private readonly MapServer _mapServer;
        private readonly DungeonsServer _dungeonServer;
        private readonly ILogger _logger;
        private readonly ISender _sender;
        private readonly IConfiguration _configuration;

        public PartyMemberKickPacketProcessor(
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
                var targetNameRaw = packet.ReadString();
                var targetName = targetNameRaw?.Trim();

                if (string.IsNullOrWhiteSpace(targetName))
                {
                    client.Send(new SystemMessagePacket("Invalid target name."));
                    _logger.Warning("PartyKick: empty target name from TamerId={TamerId}", client.TamerId);
                    return;
                }

                var party = _partyManager.FindParty(client.TamerId);
                if (party == null)
                {
                    client.Send(new SystemMessagePacket("You are not in a party."));
                    _logger.Warning("PartyKick: TamerId={TamerId} tried to kick {Target} but is not in a party", client.TamerId, targetName);
                    return;
                }

                // (Opcional) Apenas o líder pode expulsar: descomentar quando tiveres a info do líder exposta
                // if (party.LeaderId != client.TamerId)
                // {
                //     client.Send(new SystemMessagePacket("Only the party leader can kick members."));
                //     _logger.Information("PartyKick: unauthorized attempt by TamerId={TamerId} in PartyId={PartyId} to kick {Target}",
                //         client.TamerId, party.Id, targetName);
                //     return;
                // }

                // Encontra o membro alvo por nome (case-insensitive).
                var kv = party.Members.FirstOrDefault(x =>
                    x.Value?.Name != null &&
                    x.Value.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));

                if (kv.Equals(default))
                {
                    client.Send(new SystemMessagePacket($"Member '{targetName}' not found in your party."));
                    _logger.Debug("PartyKick: member '{Target}' not found in PartyId={PartyId} (by TamerId={TamerId})",
                        targetName, party.Id, client.TamerId);
                    return;
                }

                byte bannedTargetKey = (byte)kv.Key;
                var partyMemberToKick = kv.Value;

                // Não deixa expulsar quem não está realmente na party
                if (partyMemberToKick == null)
                {
                    client.Send(new SystemMessagePacket($"Member '{targetName}' is invalid."));
                    _logger.Warning("PartyKick: null member object for key '{Key}' in PartyId={PartyId}", bannedTargetKey, party.Id);
                    return;
                }

                // Opcional: impedir expulsar a si próprio via "kick" (se queres forçar Leave separado)
                // if (partyMemberToKick.Id == client.TamerId)
                // {
                //     client.Send(new SystemMessagePacket("Use Leave Party to leave the party."));
                //     return;
                // }

                var memberCount = party.Members.Count;

                if (memberCount > 2)
                {
                    await KickSingleMemberFlow(party, partyMemberToKick.Id, bannedTargetKey);
                    _logger.Information("PartyKick: PartyId={PartyId} kicked '{Target}' (Id={TargetId}) by TamerId={TamerId}",
                        party.Id, targetName, partyMemberToKick.Id, client.TamerId);
                }
                else
                {
                    var ids = party.GetMembersIdList(); // antes de remover
                    var payload = new PartyMemberKickPacket(bannedTargetKey).Serialize();

                    _dungeonServer.BroadcastForTargetTamers(ids, payload);
                    _mapServer.BroadcastForTargetTamers(ids, payload);

                    // Para cada membro ainda presente, se estiver em dungeon, teleporta para fora
                    foreach (var m in party.Members.Values.ToList())
                    {
                        var targetClient = _dungeonServer.FindClientByTamerId(m.Id);
                        if (targetClient == null) continue;
                        await TeleportOutOfDungeon(targetClient);
                    }

                    _partyManager.RemoveParty(party.Id);
                    _logger.Information("PartyKick: disbanded PartyId={PartyId} after kick of '{Target}' (members <= 2)", party.Id, targetName);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "PartyKick: exception processing kick by TamerId={TamerId}", client.TamerId);
            }
        }

        private GameClient? FindClient(long tamerId)
        {
            var c = _mapServer.FindClientByTamerId(tamerId);
            return c ?? _dungeonServer.FindClientByTamerId(tamerId);
        }
        private async Task KickSingleMemberFlow(GameParty party, long tamerIdToKick, byte bannedTargetKey)
        {
            var payload = new PartyMemberKickPacket(bannedTargetKey).Serialize();

            var bannedClient = FindClient(tamerIdToKick);
            if (bannedClient != null) bannedClient.Send(payload);

            party.RemoveMember(bannedTargetKey);

            var dungeonClient = _dungeonServer.FindClientByTamerId(tamerIdToKick);
            if (dungeonClient != null) await TeleportOutOfDungeon(dungeonClient);

            foreach (var target in party.Members.Values)
            {
                var tc = FindClient(target.Id);
                if (tc != null) tc.Send(payload);
            }
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
