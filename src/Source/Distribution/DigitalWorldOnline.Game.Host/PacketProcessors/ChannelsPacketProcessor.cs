using DigitalWorldOnline.Application.Separar.Queries;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Utils;
using MediatR;
using Serilog;
using System.Reflection;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class ChannelsPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.Channels;
        
        private readonly ISender _sender;
        private readonly ILogger _logger;

        public ChannelsPacketProcessor(
            ISender sender,
            ILogger logger)
        {
            _sender = sender;
            _logger = logger;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            _logger.Debug($"Getting available channels...");


            if (!client.DungeonMap)
            {
                var mapConfig = await _sender.Send(new GameMapConfigByMapIdQuery(client.Tamer.Location.MapId));
                var channels = new Dictionary<byte, byte>();
                for (byte i = 0; i < mapConfig.Channels; i++) channels.Add(i, 0);
                

                _logger.Debug($"Sending available channels packet...");
                client.Send(new AvailableChannelsPacket(channels));
            }
            else
            {
                var channels = new Dictionary<byte, byte>
                {
                    { 0, 30 }
                };
            }

            //return Task.CompletedTask;
        }
    }
}