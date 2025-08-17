using DigitalWorldOnline.Application;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Application.Separar.Queries;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.Character;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Map;
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
    public class WarpGateDungeonPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.WarpGateDungeon;

        private const string GameServerAddress = "GameServer:Address";
        private const string GamerServerPublic = "GameServer:PublicAddress";
        private const string GameServerPort = "GameServer:Port";

        private readonly PartyManager _partyManager;
        private readonly IConfiguration _configuration;
        private readonly AssetsLoader _assets;
        private readonly MapServer _mapServer;
        private readonly DungeonsServer _dungeonServer;
        private readonly ISender _sender;
        private readonly ILogger _logger;

        public WarpGateDungeonPacketProcessor(
            PartyManager partyManager,
            IConfiguration configuration,
            AssetsLoader assets,
            MapServer mapServer,
            ISender sender,
            ILogger logger,
            DungeonsServer dungeonServer)
        {
            _partyManager = partyManager;
            _configuration = configuration;
            _assets = assets;
            _mapServer = mapServer;
            _sender = sender;
            _logger = logger;
            _dungeonServer = dungeonServer;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);
            var portalId = packet.ReadInt();

            // _logger.Information($"Dungeon PortalId: {portalId}");

            // --------------------------------------------------------------------
            // Procura portal com checks de null (antes de aceder a propriedades)
            // --------------------------------------------------------------------
            var portal = _assets.Portal.FirstOrDefault(x => x.Id == portalId);

            if (portal == null)
            {
                client.Send(new SystemMessagePacket($"Portal {portalId} not found."));
                _logger.Error($"Portal id {portalId} not found.");

                var fallbackMapId = client.Tamer.Location.MapId;

                var mapConfig = await _sender.Send(new GameMapConfigByMapIdQuery(fallbackMapId));
                var waypoints = await _sender.Send(new MapRegionListAssetsByMapIdQuery(fallbackMapId));

                if (mapConfig == null || waypoints == null || !waypoints.Regions.Any())
                {
                    client.Send(new SystemMessagePacket($"Map information not found for {fallbackMapId}"));
                    _logger.Error($"Map information not found for {fallbackMapId}");
                    return;
                }

                // Volta para o primeiro waypoint do mapa atual
                _mapServer.RemoveClient(client);

                var destination = waypoints.Regions.First();

                client.Tamer.NewLocation(fallbackMapId, destination.X, destination.Y);
                await _sender.Send(new UpdateCharacterLocationCommand(client.Tamer.Location));

                client.Tamer.Partner.NewLocation(fallbackMapId, destination.X, destination.Y);
                await _sender.Send(new UpdateDigimonLocationCommand(client.Tamer.Partner.Location));

                client.Tamer.UpdateState(CharacterStateEnum.Loading);
                await _sender.Send(new UpdateCharacterStateCommand(client.TamerId, CharacterStateEnum.Loading));

                client.Send(new MapSwapPacket(
                    _configuration[GamerServerPublic],
                    _configuration[GameServerPort],
                    fallbackMapId, destination.X, destination.Y));
                return;
            }

            // --------------------------------------------------------------------
            // Processamento de custos do portal (itens / bits) se existirem
            // --------------------------------------------------------------------
            var npcPortals = _assets.Npcs.FirstOrDefault(x => x.NpcId == portal.NpcId)?.Portals?.ToList();

            if (npcPortals != null && npcPortals.Count > 0)
            {
                var npcPortalsAssets = npcPortals.SelectMany(x => x.PortalsAsset ?? Enumerable.Empty<dynamic>()).ToList();

                // Valida o índice do portal
                if (portal.PortalIndex >= 0 && portal.PortalIndex < npcPortalsAssets.Count)
                {
                    var removeInfo = npcPortalsAssets[portal.PortalIndex];

                    // Alguns assets usam arrays fixos; aqui iteramos com segurança
                    var resources = (IEnumerable<dynamic>)(removeInfo?.npcPortalsAsset ?? Array.Empty<dynamic>());

                    foreach (var res in resources)
                    {
                        try
                        {
                            // Nomes conforme os assets: Type, ItemId...
                            var type = (NpcResourceTypeEnum)res.Type;

                            switch (type)
                            {
                                case NpcResourceTypeEnum.Money:
                                    {
                                        // Em muitos assets, ItemId carrega o "valor em bits" requerido
                                        int bitsToRemove = (int)res.ItemId;
                                        client.Tamer.Inventory.RemoveBits(bitsToRemove);

                                        // Mantém consistente com outros pontos do código (envia id e valor atual)
                                        await _sender.Send(new UpdateItemListBitsCommand(
                                            client.Tamer.Inventory.Id,
                                            client.Tamer.Inventory.Bits));
                                    }
                                    break;

                                case NpcResourceTypeEnum.Item:
                                    {
                                        int itemId = (int)res.ItemId;
                                        var targetItem = client.Tamer.Inventory.FindItemById(itemId);

                                        if (targetItem != null)
                                        {
                                            client.Tamer.Inventory.RemoveOrReduceItem(targetItem, 1);
                                            // Se o item saiu completamente, o UpdateItemCommand pode falhar;
                                            // mas mantemos para refletir a remoção (é o padrão do projeto).
                                            await _sender.Send(new UpdateItemCommand(targetItem));
                                        }
                                    }
                                    break;

                                default:
                                    // Tipos não usados / não suportados — ignora
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "Failed to process portal resource payment for portal {PortalId}.", portal.Id);
                        }
                    }
                }
                else
                {
                    _logger.Warning("Portal index {Index} out of range for NPC portal assets (PortalId={PortalId}, NpcId={NpcId}).",
                        portal.PortalIndex, portal.Id, portal.NpcId);
                }
            }

            // --------------------------------------------------------------------
            // Verifica se o cliente está no MapServer ou num DungeonsServer
            // (Royal Base tem regras locais para portais entre pisos)
            // --------------------------------------------------------------------
            var mapClient = _mapServer.FindClientByTamerId(client.TamerId);

            if (mapClient == null)
            {
                var tamerMap = _dungeonServer.Maps
                    .FirstOrDefault(x => x.Clients.Exists(y => y.TamerId == client.TamerId));

                _logger.Verbose("Verifying if is Royal Base Map: {IsRoyalBase}",
                    tamerMap?.IsRoyalBase.ToString() ?? "null");

                // Regras específicas Royal Base (quando portal é inter-mapa mas mapa é especial)
                if (!portal.IsLocal && tamerMap != null && tamerMap.IsRoyalBase)
                {
                    int currentMapId = tamerMap.MapId;

                    _logger.Verbose(
                        "Going MapID: {DestMapId} and is allowed to enter floor 3 {Allowed}",
                        portal.DestinationMapId,
                        (tamerMap?.RoyalBaseMap?.AllowUsingPortalFromFloorTwoToFloorThree == false
                         || tamerMap?.RoyalBaseMap?.AllowUsingPortalFromFloorTwoToFloorThree == null).ToString());

                    // Piso 1 -> 2 (1702) bloqueado
                    if ((tamerMap?.RoyalBaseMap?.AllowUsingPortalFromFloorOneToFloorTwo == false
                         || tamerMap?.RoyalBaseMap?.AllowUsingPortalFromFloorOneToFloorTwo == null)
                        && portal.DestinationMapId == 1702)
                    {
                        const int LocationX = 32000;
                        const int LocationY = 32000;

                        client.Tamer.NewLocation(currentMapId, LocationX, LocationY);
                        await _sender.Send(new UpdateCharacterLocationCommand(client.Tamer.Location));

                        client.Tamer.Partner.NewLocation(currentMapId, LocationX, LocationY);
                        await _sender.Send(new UpdateDigimonLocationCommand(client.Tamer.Partner.Location));

                        client.Send(new LocalMapSwapPacket(
                            client.Tamer.GeneralHandler,
                            client.Tamer.Partner.GeneralHandler,
                            LocationX, LocationY, LocationX, LocationY));

                        return;
                    }

                    // Piso 2 -> 3 (1703) bloqueado
                    if ((tamerMap?.RoyalBaseMap?.AllowUsingPortalFromFloorTwoToFloorThree == false
                         || tamerMap?.RoyalBaseMap?.AllowUsingPortalFromFloorTwoToFloorThree == null)
                        && portal.DestinationMapId == 1703)
                    {
                        const int LocationX = 34814;
                        const int LocationY = 30686;

                        client.Tamer.NewLocation(currentMapId, LocationX, LocationY);
                        await _sender.Send(new UpdateCharacterLocationCommand(client.Tamer.Location));

                        client.Tamer.Partner.NewLocation(currentMapId, LocationX, LocationY);
                        await _sender.Send(new UpdateDigimonLocationCommand(client.Tamer.Partner.Location));

                        client.Send(new LocalMapSwapPacket(
                            client.Tamer.GeneralHandler,
                            client.Tamer.Partner.GeneralHandler,
                            LocationX, LocationY, LocationX, LocationY));

                        return;
                    }
                }
            }

            // --------------------------------------------------------------------
            // Teleporte local (mesmo servidor / mapa “local”)
            // --------------------------------------------------------------------
            if (portal.IsLocal)
            {
                client.Tamer.NewLocation(portal.DestinationMapId, portal.DestinationX, portal.DestinationY);
                await _sender.Send(new UpdateCharacterLocationCommand(client.Tamer.Location));

                client.Tamer.Partner.NewLocation(portal.DestinationMapId, portal.DestinationX, portal.DestinationY);
                await _sender.Send(new UpdateDigimonLocationCommand(client.Tamer.Partner.Location));

                client.Send(new LocalMapSwapPacket(
                    client.Tamer.GeneralHandler, client.Tamer.Partner.GeneralHandler,
                    portal.DestinationX, portal.DestinationY,
                    portal.DestinationX, portal.DestinationY));

                return;
            }

            // --------------------------------------------------------------------
            // Teleporte entre mapas (MapSwap para endereço/porta do GameServer)
            // --------------------------------------------------------------------
            _mapServer.RemoveClient(client);

            client.Tamer.NewLocation(portal.DestinationMapId, portal.DestinationX, portal.DestinationY);
            await _sender.Send(new UpdateCharacterLocationCommand(client.Tamer.Location));

            client.Tamer.Partner.NewLocation(portal.DestinationMapId, portal.DestinationX, portal.DestinationY);
            await _sender.Send(new UpdateDigimonLocationCommand(client.Tamer.Partner.Location));

            client.Tamer.UpdateState(CharacterStateEnum.Loading);
            await _sender.Send(new UpdateCharacterStateCommand(client.TamerId, CharacterStateEnum.Loading));

            client.Tamer.SetCurrentChannel(0);
            client.SetGameQuit(false);

            client.Send(new MapSwapPacket(
                _configuration[GamerServerPublic],
                _configuration[GameServerPort],
                client.Tamer.Location.MapId,
                client.Tamer.Location.X,
                client.Tamer.Location.Y));

            // --------------------------------------------------------------------
            // Atualiza party e notifica membros
            // --------------------------------------------------------------------
            var partyObj = _partyManager.FindParty(client.TamerId);

            if (partyObj != null)
            {
                partyObj.UpdateMember(partyObj[client.TamerId], client.Tamer);

                foreach (var target in partyObj.Members.Values)
                {
                    var targetClient = _mapServer.FindClientByTamerId(target.Id)
                                     ?? _dungeonServer.FindClientByTamerId(target.Id);

                    if (targetClient == null) continue;
                    if (target.Id != client.Tamer.Id)
                        targetClient.Send(new PartyMemberWarpGatePacket(partyObj[client.TamerId]).Serialize());
                }

                client.Send(new PartyMemberListPacket(
                    partyObj,
                    client.TamerId,
                    (byte)(partyObj.Members.Count - 1)).Serialize());
            }

            // client.Send(new SendHandler(client.Tamer.GeneralHandler));
        }
    }
}
