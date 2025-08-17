using System;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Game.Managers;
using DigitalWorldOnline.GameHost;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class PartyLeaderChangePacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.PartyLeaderChange;

        private readonly PartyManager _partyManager;
        private readonly MapServer _mapServer;
        private readonly DungeonsServer _dungeonServer;
        private readonly ILogger _logger;

        public PartyLeaderChangePacketProcessor(
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
                var newLeaderSlot = packet.ReadInt();

                if (newLeaderSlot < 0)
                {
                    _logger.Warning("PartyLeaderChange: invalid slot {Slot} from TamerId={TamerId}", newLeaderSlot, client.TamerId);
                    return Task.CompletedTask;
                }

                var party = _partyManager.FindParty(client.TamerId);
                if (party == null)
                {
                    _logger.Warning("PartyLeaderChange: TamerId={TamerId} not in a party (slot requested={Slot})", client.TamerId, newLeaderSlot);
                    return Task.CompletedTask;
                }

                // (Opcional) Autorização: apenas o líder pode mudar
                // Descomenta quando tiveres uma forma de identificar o líder.
                // if (party.LeaderId != client.TamerId)
                // {
                //     _logger.Information("PartyLeaderChange: unauthorized attempt by TamerId={TamerId} in PartyId={PartyId} to set slot {Slot} as leader",
                //         client.TamerId, party.Id, newLeaderSlot);
                //     return Task.CompletedTask;
                // }

                // Se ChangeLeader lançar exceção para slot inválido, capturamos no catch principal.
                party.ChangeLeader(newLeaderSlot);

                var payload = new PartyLeaderChangedPacket(newLeaderSlot).Serialize();
                var notified = 0;

                foreach (var memberId in party.GetMembersIdList())
                {
                    var targetClient = FindClientByTamerId(memberId);
                    if (targetClient == null) continue;

                    targetClient.Send(payload);
                    notified++;
                }

                _logger.Information(
                    "PartyLeaderChange: PartyId={PartyId} leader set to slot {Slot} by TamerId={TamerId}. Notified={Notified}",
                    party.Id, newLeaderSlot, client.TamerId, notified
                );
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "PartyLeaderChange: exception processing packet for TamerId={TamerId}", client.TamerId);
            }

            return Task.CompletedTask;
        }

        private GameClient? FindClientByTamerId(long tamerId)
        {
            var c = _mapServer.FindClientByTamerId(tamerId);
            return c ?? _dungeonServer.FindClientByTamerId(tamerId);
        }
    }
}
