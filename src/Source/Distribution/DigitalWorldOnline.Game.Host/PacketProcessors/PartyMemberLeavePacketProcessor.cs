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

        public PartyMemberLeavePacketProcessor(PartyManager partyManager, MapServer mapServer,
            ILogger logger, ISender sender, IConfiguration configuration,
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
            var packet = new GamePacketReader(packetData);

            var targetName = packet.ReadString();

            var party = _partyManager.FindParty(client.TamerId);

            if (party != null)
            {
                var membersList = party.GetMembersIdList();
                var leaveTargetKey = party[client.TamerId].Key;

                if (party.LeaderSlot == leaveTargetKey && party.Members.Count > 2)
                {
                    //_logger.Information($"Leader {client.Tamer.Name} left the party !! Party Size ({party.Members.Count})");

                    _mapServer.BroadcastForTargetTamers(membersList, new PartyMemberLeavePacket(leaveTargetKey).Serialize());
                    _dungeonServer.BroadcastForTargetTamers(membersList, new PartyMemberLeavePacket(leaveTargetKey).Serialize());

                    party.RemoveMember(leaveTargetKey);

                    var randomIndex = new Random().Next(party.Members.Count);
                    //_logger.Information($"Total Party Members now: {party.Members.Count} | new Leader index: {randomIndex}");
                    //_logger.Information($"New Party Leader Slot: {party.LeaderSlot}");

                    var sortedPlayer = party.Members.ElementAt(randomIndex).Key;

                    party.ChangeLeader(sortedPlayer);

                    _mapServer.BroadcastForTargetTamers(membersList, new PartyLeaderChangedPacket(sortedPlayer).Serialize());
                    _dungeonServer.BroadcastForTargetTamers(membersList, new PartyLeaderChangedPacket(sortedPlayer).Serialize());
                }
                else if (party.Members.Count <= 2)
                {
                    //_logger.Information($"{client.Tamer.Name} left the party !! Party Size ({party.Members.Count})");

                    var mapClient = _mapServer.FindClientByTamerId(client.TamerId);

                    _mapServer.BroadcastForTargetTamers(membersList, new PartyMemberLeavePacket(leaveTargetKey).Serialize());
                    _dungeonServer.BroadcastForTargetTamers(membersList, new PartyMemberLeavePacket(leaveTargetKey).Serialize());

                    //_logger.Information($"Removing Member !!");
                    party.RemoveMember(leaveTargetKey);

                    foreach (var target in party.Members.Values)
                    {
                        if (mapClient == null)
                        {
                            mapClient = _dungeonServer.FindClientByTamerId(client.TamerId);

                            // -- Teleport player outside of Dungeon ---------------------------------
                            var map = UtilitiesFunctions.MapGroup(mapClient.Tamer.Location.MapId);

                            var mapConfig = await _sender.Send(new GameMapConfigByMapIdQuery(mapClient.Tamer.Location.MapId));
                            var waypoints = await _sender.Send(new MapRegionListAssetsByMapIdQuery(map));

                            if (mapConfig == null || waypoints == null || !waypoints.Regions.Any())
                            {
                                client.Send(new SystemMessagePacket($"Map information not found for map Id {map}."));
                                _logger.Warning($"Map information not found for map Id {map} on character {client.TamerId} jump booster.");
                                return;
                            }

                            var mapRegionIndex = mapConfig.MapRegionindex;
                            var destination = waypoints.Regions.FirstOrDefault(x => x.Index == mapRegionIndex);

                            _dungeonServer.RemoveClient(mapClient);

                            mapClient.Tamer.NewLocation(map, destination.X, destination.Y);
                            await _sender.Send(new UpdateCharacterLocationCommand(mapClient.Tamer.Location));

                            mapClient.Tamer.Partner.NewLocation(map, destination.X, destination.Y);
                            await _sender.Send(new UpdateDigimonLocationCommand(mapClient.Tamer.Partner.Location));

                            mapClient.Tamer.UpdateState(CharacterStateEnum.Loading);
                            await _sender.Send(new UpdateCharacterStateCommand(mapClient.TamerId, CharacterStateEnum.Loading));

                            mapClient.SetGameQuit(false);

                            mapClient.Send(new MapSwapPacket(_configuration[GamerServerPublic], _configuration[GameServerPort],
                                mapClient.Tamer.Location.MapId, mapClient.Tamer.Location.X, mapClient.Tamer.Location.Y));
                        }

                    }

                    _partyManager.RemoveParty(party.Id);
                    //_logger.Information($"Party removed !!");
                }
                else
                {
                    //_logger.Information($"Member {client.Tamer.Name} left the party !! Party Size ({party.Members.Count})");

                    var partyMember = party[client.TamerId].Value;

                    _mapServer.BroadcastForTargetTamers(membersList, new PartyMemberLeavePacket(leaveTargetKey).Serialize());
                    _dungeonServer.BroadcastForTargetTamers(membersList, new PartyMemberLeavePacket(leaveTargetKey).Serialize());

                    party.RemoveMember(leaveTargetKey);
                    //_logger.Information($"Member removed !!");

                    // -------------------------------------------------------

                    var dungeonClient = _dungeonServer.FindClientByTamerId(partyMember.Id);

                    if (dungeonClient != null)
                    {
                        var map = UtilitiesFunctions.MapGroup(dungeonClient.Tamer.Location.MapId);

                        var mapConfig = await _sender.Send(new GameMapConfigByMapIdQuery(dungeonClient.Tamer.Location.MapId));
                        var waypoints = await _sender.Send(new MapRegionListAssetsByMapIdQuery(map));

                        if (mapConfig == null || waypoints == null || !waypoints.Regions.Any())
                        {
                            client.Send(new SystemMessagePacket($"Map information not found for map Id {map}."));
                            _logger.Warning($"Map information not found for map Id {map} on character {client.TamerId} jump booster.");
                            return;
                        }

                        var mapRegionIndex = mapConfig.MapRegionindex;
                        var destination = waypoints.Regions.FirstOrDefault(x => x.Index == mapRegionIndex);

                        _dungeonServer.RemoveClient(dungeonClient);

                        dungeonClient.Tamer.NewLocation(map, destination.X, destination.Y);
                        await _sender.Send(new UpdateCharacterLocationCommand(dungeonClient.Tamer.Location));

                        dungeonClient.Tamer.Partner.NewLocation(map, destination.X, destination.Y);
                        await _sender.Send(new UpdateDigimonLocationCommand(dungeonClient.Tamer.Partner.Location));

                        dungeonClient.Tamer.UpdateState(CharacterStateEnum.Loading);
                        await _sender.Send(new UpdateCharacterStateCommand(dungeonClient.TamerId, CharacterStateEnum.Loading));

                        dungeonClient.SetGameQuit(false);

                        dungeonClient.Send(new MapSwapPacket(_configuration[GamerServerPublic], _configuration[GameServerPort],
                            dungeonClient.Tamer.Location.MapId, dungeonClient.Tamer.Location.X, dungeonClient.Tamer.Location.Y));
                    }

                    foreach (var target in party.Members.Values)
                    {
                        party.RemoveMember(leaveTargetKey);
                        //_logger.Information($"Member removed for each !!");
                    }

                }

            }
            else
            {
                _logger.Error($"Tamer {client.Tamer.Name} left from the party but he/she was not in the party.");
            }
        }
    }
}