using DigitalWorldOnline.Application.Separar.Queries;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.GameHost;
using MediatR;
using Serilog;
using System;
using System.Threading.Tasks;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class GuildInviteDenyPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.GuildInviteDeny;

        private readonly MapServer _mapServer;
        private readonly ILogger _logger;
        private readonly ISender _sender;

        public GuildInviteDenyPacketProcessor(
            MapServer mapServer,
            ILogger logger,
            ISender sender)
        {
            _mapServer = mapServer;
            _logger = logger;
            _sender = sender;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            if (client == null)
            {
                _logger.Warning("GuildInviteDeny: client is null.");
                return;
            }

            var packet = new GamePacketReader(packetData);

            var guildId = packet.ReadInt(); // reservado para validações futuras/logs
            var senderName = (packet.ReadString() ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(senderName))
            {
                _logger.Warning("GuildInviteDeny: senderName is empty. TamerId={TamerId}", client.TamerId);
                return;
            }

            if (client.Tamer == null)
            {
                // <-- AQUI a correção: usar TamerId ao invés de client.Id
                _logger.Warning("GuildInviteDeny: client.Tamer is null. TamerId={TamerId}", client.TamerId);
                return;
            }

            _logger.Debug("GuildInviteDeny: searching inviter by name {SenderName}...", senderName);

            // 1) tentar online primeiro
            var inviterClient = _mapServer.FindClientByTamerName(senderName);
            if (inviterClient?.Tamer != null)
            {
                try
                {
                    _logger.Verbose("GuildInviteDeny: {InviteeId} denied invite from online {InviterId}. GuildId={GuildId}",
                        client.TamerId, inviterClient.TamerId, guildId);

                    _mapServer.BroadcastForUniqueTamer(
                        inviterClient.TamerId,
                        new GuildInviteDenyPacket(client.Tamer.Name).Serialize()
                    );
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "GuildInviteDeny: failed to notify online inviter {InviterId}.", inviterClient.TamerId);
                }
                return;
            }

            // 2) fallback: buscar por nome (pode estar offline)
            try
            {
                var targetCharacter = await _sender.Send(new CharacterByNameQuery(senderName));
                if (targetCharacter != null)
                {
                    _logger.Verbose("GuildInviteDeny: {InviteeId} denied invite from {InviterCharId} (offline). GuildId={GuildId}",
                        client.TamerId, targetCharacter.Id, guildId);

                    _mapServer.BroadcastForUniqueTamer(
                        targetCharacter.Id,
                        new GuildInviteDenyPacket(client.Tamer.Name).Serialize()
                    );
                }
                else
                {
                    _logger.Warning("GuildInviteDeny: inviter not found by name {SenderName}.", senderName);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "GuildInviteDeny: error resolving inviter by name {SenderName}.", senderName);
            }
        }
    }
}
