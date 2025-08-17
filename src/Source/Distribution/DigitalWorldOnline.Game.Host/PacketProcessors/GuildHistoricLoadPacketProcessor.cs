using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Packets.GameServer;
using Serilog;
using System;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class GuildHistoricLoadPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.GuildHistoric;

        private readonly ILogger _logger;

        public GuildHistoricLoadPacketProcessor(ILogger logger)
        {
            _logger = logger;
        }

        public Task Process(GameClient client, byte[] packetData)
        {
            if (client == null)
            {
                _logger.Warning("GuildHistoricLoad: client is null.");
                return Task.CompletedTask;
            }

            var tamer = client.Tamer;

            if (tamer?.Guild == null)
            {
                _logger.Debug("GuildHistoricLoad: character {TamerId} has no guild or tamer is null.", client.TamerId);
                return Task.CompletedTask;
            }

            var historic = tamer.Guild.Historic;
            if (historic == null)
            {
                _logger.Debug("GuildHistoricLoad: guild historic is null for character {TamerId}.", client.TamerId);
                return Task.CompletedTask;
            }

            try
            {
                _logger.Debug("GuildHistoricLoad: sending guild historic to character {TamerId}...", client.TamerId);
                client.Send(new GuildHistoricPacket(historic));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "GuildHistoricLoad: failed to send guild historic to character {TamerId}.", client.TamerId);
            }

            return Task.CompletedTask;
        }
    }
}
