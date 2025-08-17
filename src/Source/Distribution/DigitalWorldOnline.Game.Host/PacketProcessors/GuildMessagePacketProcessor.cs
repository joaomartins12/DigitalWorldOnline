using DigitalWorldOnline.Application.Separar.Commands.Create;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Chat;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.GameHost;
using MediatR;
using Serilog;
using System.IO;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class GuildMessagePacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.GuildMessage;

        private readonly MapServer _mapServer;
        private readonly DungeonsServer _dungeonServer;
        private readonly ILogger _logger;
        private readonly ISender _sender;

        public GuildMessagePacketProcessor(
            MapServer mapServer,
            ILogger logger,
            ISender sender,
            DungeonsServer dungeonServer)
        {
            _mapServer = mapServer;
            _logger = logger;
            _sender = sender;
            _dungeonServer = dungeonServer;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);
            var message = packet.ReadString();

            var guild = client.Tamer.Guild;
            if (guild != null)
            {
                // Serializa uma vez só
                var payload = new GuildMessagePacket(client.Tamer.Name, message).Serialize();

                foreach (var memberId in guild.GetGuildMembersIdList())
                {
                    var targetPlayer = _mapServer.FindClientByTamerId(memberId)
                                       ?? _dungeonServer.FindClientByTamerId(memberId);

                    // Se o membro estiver offline, apenas ignora
                    targetPlayer?.Send(payload);
                }

                _logger.Verbose($"Character {client.TamerId} sent chat to guild {guild.Id} with message {message}.");
                await _mapServer.CallDiscord(message, client, "1eff00", guild.Name);

                await _sender.Send(new CreateChatMessageCommand(ChatMessageModel.Create(client.TamerId, message)));
            }
            else
            {
                client.Send(new SystemMessagePacket("You need to be in a guild to send guild messages."));
                _logger.Warning($"Character {client.TamerId} sent guild message but was not in a guild.");
            }
        }
    }
}
