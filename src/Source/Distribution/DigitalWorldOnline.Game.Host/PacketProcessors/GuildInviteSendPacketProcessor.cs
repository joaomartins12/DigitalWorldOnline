using DigitalWorldOnline.Application.Separar.Queries;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.Character;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.GameHost;
using MediatR;
using Serilog;
using System;
using System.Threading.Tasks;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class GuildInviteSendPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.GuildInvite;

        private readonly MapServer _mapServer;
        private readonly ILogger _logger;
        private readonly ISender _sender;

        public GuildInviteSendPacketProcessor(
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
            // Proteções básicas
            if (client == null)
            {
                _logger.Warning("GuildInviteSend: client is null.");
                return;
            }
            if (client.Tamer == null)
            {
                _logger.Warning("GuildInviteSend: client.Tamer is null. TamerId={TamerId}", client.TamerId);
                return;
            }
            if (client.Tamer.Guild == null)
            {
                _logger.Debug("GuildInviteSend: Tamer {TamerId} is not in a guild.", client.TamerId);
                client.Send(new SystemMessagePacket("You must be in a guild to invite players."));
                return;
            }

            var packet = new GamePacketReader(packetData);
            var targetName = (packet.ReadString() ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(targetName))
            {
                _logger.Warning("GuildInviteSend: empty target name. TamerId={TamerId}", client.TamerId);
                client.Send(new SystemMessagePacket("Invalid target name."));
                return;
            }

            if (string.Equals(targetName, client.Tamer.Name, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug("GuildInviteSend: self invite blocked. TamerId={TamerId}", client.TamerId);
                client.Send(new SystemMessagePacket("You cannot invite yourself."));
                return;
            }

            _logger.Debug("GuildInviteSend: searching character by name {TargetName}...", targetName);

            // Online primeiro para garantir entrega imediata
            var targetOnlineClient = _mapServer.FindClientByTamerName(targetName);

            // Normalizamos para trabalhar só com Id/State
            int? targetId = null;
            CharacterStateEnum targetState = default;

            if (targetOnlineClient != null && targetOnlineClient.Tamer != null)
            {
                // cast explícito para evitar CS0266 (TamerId costuma ser long)
                targetId = (int)targetOnlineClient.TamerId;
                targetState = CharacterStateEnum.Ready; // online ⇒ Ready
            }
            else
            {
                // Fallback via query
                var targetCharacter = await _sender.Send(new CharacterByNameQuery(targetName));
                if (targetCharacter != null)
                {
                    targetId = (int)targetCharacter.Id; // cast explícito de long para int
                    targetState = targetCharacter.State;
                }
            }

            if (targetId == null || targetState != CharacterStateEnum.Ready)
            {
                _logger.Verbose("GuildInviteSend: {InviterId} invited {TargetName} but target not connected.", client.TamerId, targetName);
                _logger.Debug("GuildInviteSend: sending fail(TargetNotConnected) to {InviterId}...", client.TamerId);
                client.Send(new GuildInviteFailPacket(GuildInviteFailEnum.TargetNotConnected, targetName));
                return;
            }

            // Já está numa guild?
            _logger.Debug("GuildInviteSend: checking guild by character id {TargetId}...", targetId);
            var targetGuild = await _sender.Send(new GuildByCharacterIdQuery(targetId.Value));
            if (targetGuild != null)
            {
                _logger.Verbose("GuildInviteSend: {InviterId} invited {TargetId} but target is in another guild.", client.TamerId, targetId);
                _logger.Debug("GuildInviteSend: sending fail(TargetInAnotherGuild) to {InviterId}...", client.TamerId);
                client.Send(new GuildInviteFailPacket(GuildInviteFailEnum.TargetInAnotherGuild, targetName));
                return;
            }

            // Entrega do convite (garantia: tenta pelo online; senão por Id)
            _logger.Verbose("GuildInviteSend: {InviterId} invited {TargetId}.", client.TamerId, targetId);

            try
            {
                var deliverId = targetOnlineClient != null ? (int)targetOnlineClient.TamerId : targetId.Value;

                _logger.Debug("GuildInviteSend: delivering invite to {DeliverId}...", deliverId);
                _mapServer.BroadcastForUniqueTamer(
                    deliverId,
                    new GuildInviteSuccessPacket(client.Tamer).Serialize()
                );

                client.Send(new SystemMessagePacket($"Guild invite sent to {targetName}."));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "GuildInviteSend: failed to deliver invite to target {TargetId}.", targetId);
                client.Send(new SystemMessagePacket("Failed to deliver guild invite."));
            }
        }
    }
}
