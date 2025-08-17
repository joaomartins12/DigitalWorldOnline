using System;
using System.Collections.Generic;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Enums.Party;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Game.Managers;
using DigitalWorldOnline.GameHost;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class PartyChangeLootTypePacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.PartyConfigChange;

        private readonly PartyManager _partyManager;
        private readonly MapServer _mapServer;
        private readonly DungeonsServer _dungeonServer;
        private readonly ILogger _logger;

        public PartyChangeLootTypePacketProcessor(
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

                var lootTypeRaw = packet.ReadInt();
                var rareTypeRaw = packet.ReadByte();

                // Validação de enums recebidos
                if (!Enum.IsDefined(typeof(PartyLootShareTypeEnum), lootTypeRaw) ||
                    !Enum.IsDefined(typeof(PartyLootShareRarityEnum), (int)rareTypeRaw))
                {
                    _logger.Warning(
                        "PartyChangeLootType: invalid params from TamerId={TamerId} (lootType={LootType}, rareType={RareType})",
                        client.TamerId, lootTypeRaw, rareTypeRaw
                    );
                    return Task.CompletedTask;
                }

                var lootType = (PartyLootShareTypeEnum)lootTypeRaw;
                var rareType = (PartyLootShareRarityEnum)rareTypeRaw;

                var party = _partyManager.FindParty(client.TamerId);
                if (party == null)
                {
                    _logger.Debug(
                        "PartyChangeLootType: no party found for TamerId={TamerId}",
                        client.TamerId
                    );
                    return Task.CompletedTask;
                }

                // (Opcional) Autorização: somente líder pode alterar
                // Se o teu GameParty expõe LeaderId/IsLeader, ativa o check abaixo.
                // if (party.LeaderId != client.TamerId) {
                //     _logger.Information("PartyChangeLootType: unauthorized attempt by TamerId={TamerId} in PartyId={PartyId}", client.TamerId, party.Id);
                //     return Task.CompletedTask;
                // }

                party.ChangeLootType(lootType, rareType);

                // Broadcast para todos na party (mapa ou dungeon)
                var notified = 0;
                foreach (var member in party.Members.Values)
                {
                    var targetClient = FindClientByTamerId(member.Id);
                    if (targetClient == null) continue;

                    targetClient.Send(new PartyChangeLootTypePacket(lootType, rareType));
                    notified++;
                }

                _logger.Information(
                    "PartyChangeLootType: PartyId={PartyId} set to LootType={LootType}, RareType={RareType} by TamerId={TamerId}. Notified={Notified}",
                    party.Id, lootType, rareType, client.TamerId, notified
                );
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "PartyChangeLootType: exception processing packet for TamerId={TamerId}", client.TamerId);
            }

            return Task.CompletedTask;
        }

        private GameClient? FindClientByTamerId(long tamerId)
        {
            var c = _mapServer.FindClientByTamerId(tamerId);
            if (c != null) return c;
            return _dungeonServer.FindClientByTamerId(tamerId);
        }
    }
}
