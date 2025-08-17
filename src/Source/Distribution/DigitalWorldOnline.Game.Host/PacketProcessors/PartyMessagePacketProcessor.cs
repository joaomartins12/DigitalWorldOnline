using System;
using System.Linq;
using System.Threading.Tasks;
using DigitalWorldOnline.Application.Separar.Commands.Create;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Chat;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Game.Managers;
using DigitalWorldOnline.GameHost;
using MediatR;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class PartyMessagePacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.PartyMessage;

        private readonly PartyManager _partyManager;
        private readonly MapServer _mapServer;
        private readonly DungeonsServer _dungeonServer;
        private readonly ILogger _logger;
        private readonly ISender _sender;

        // limite razoável para chat (ajusta se tiveres um valor global)
        private const int MaxMessageLength = 256;

        public PartyMessagePacketProcessor(
            PartyManager partyManager,
            MapServer mapServer,
            DungeonsServer dungeonsServer,
            ILogger logger,
            ISender sender)
        {
            _partyManager = partyManager;
            _mapServer = mapServer;
            _dungeonServer = dungeonsServer;
            _logger = logger;            // <-- removida duplicação
            _sender = sender;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            try
            {
                var packet = new GamePacketReader(packetData);
                var message = packet.ReadString() ?? string.Empty;

                // normalização básica
                message = message.Replace("\r", " ").Replace("\n", " ").Trim();
                if (message.Length > MaxMessageLength)
                    message = message.Substring(0, MaxMessageLength);

                if (string.IsNullOrWhiteSpace(message))
                {
                    // silencioso ou responde com system msg, como preferires:
                    // client.Send(new SystemMessagePacket("Empty party message."));
                    _logger.Debug("PartyMessage: empty message from TamerId={TamerId}", client.TamerId);
                    return;
                }

                var party = _partyManager.FindParty(client.TamerId);
                if (party == null)
                {
                    client.Send(new SystemMessagePacket("You need to be in a party to send party messages."));
                    _logger.Warning("PartyMessage: TamerId={TamerId} sent party msg but is not in a party.", client.TamerId);
                    return;
                }

                // prepara payload uma única vez
                var payload = new PartyMessagePacket(client.Tamer.Name, message).Serialize();

                // snapshot dos IDs para evitar mutações durante o loop
                var memberIds = party.GetMembersIdList();
                var delivered = 0;

                foreach (var memberId in memberIds)
                {
                    var target = _mapServer.FindClientByTamerId(memberId)
                                ?? _dungeonServer.FindClientByTamerId(memberId);

                    if (target == null) continue;

                    target.Send(payload);
                    delivered++;
                }

                _logger.Verbose("PartyMessage: TamerId={TamerId} -> PartyId={PartyId} delivered={Delivered}",
                    client.TamerId, party.Id, delivered);

                // persistência (mantive a tua chamada original)
                await _sender.Send(new CreateChatMessageCommand(ChatMessageModel.Create(client.TamerId, message)));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "PartyMessage: exception processing message from TamerId={TamerId}", client.TamerId);
            }
        }
    }
}
