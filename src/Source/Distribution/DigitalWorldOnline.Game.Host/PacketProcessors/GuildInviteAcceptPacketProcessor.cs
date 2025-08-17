using AutoMapper;
using DigitalWorldOnline.Application.Separar.Commands.Create;
using DigitalWorldOnline.Application.Separar.Queries;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Character;
using DigitalWorldOnline.Commons.Models.Mechanics;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Packets.MapServer;
using DigitalWorldOnline.GameHost;
using MediatR;
using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class GuildInviteAcceptPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.GuildInviteAccept;

        private readonly MapServer _mapServer;
        private readonly ILogger _logger;
        private readonly ISender _sender;
        private readonly IMapper _mapper; // Mantido caso seja necessário futuramente

        public GuildInviteAcceptPacketProcessor(
            MapServer mapServer,
            ILogger logger,
            ISender sender,
            IMapper mapper)
        {
            _mapServer = mapServer;
            _logger = logger;
            _sender = sender;
            _mapper = mapper;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            if (client == null)
            {
                _logger.Warning("GuildInviteAccept: client is null.");
                return;
            }

            var packet = new GamePacketReader(packetData);
            var guildId = packet.ReadInt();
            var inviterName = (packet.ReadString() ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(inviterName))
            {
                _logger.Warning("GuildInviteAccept: inviterName is empty.");
                client.Send(new SystemMessagePacket("Invalid inviter name."));
                return;
            }

            if (client.Tamer == null)
            {
                _logger.Warning("GuildInviteAccept: client.Tamer is null for {TamerId}.", client.TamerId);
                client.Send(new SystemMessagePacket("Unable to process invite right now."));
                return;
            }

            // Já está em guild?
            if (client.Tamer.Guild != null)
            {
                _logger.Information("GuildInviteAccept: Tamer {TamerId} already in a guild ({GuildId}).", client.TamerId, client.Tamer.Guild.Id);
                client.Send(new SystemMessagePacket("You are already in a guild."));
                return;
            }

            _logger.Debug("GuildInviteAccept: searching inviter by name {InviterName}...", inviterName);
            var inviterClient = _mapServer.FindClientByTamerName(inviterName);
            if (inviterClient == null || inviterClient.Tamer == null)
            {
                _logger.Warning("GuildInviteAccept: inviter {InviterName} not found or has no tamer.", inviterName);
                client.Send(new SystemMessagePacket($"Character not found with name {inviterName}."));
                return;
            }

            var targetGuild = inviterClient.Tamer.Guild;
            if (targetGuild == null)
            {
                _logger.Warning("GuildInviteAccept: inviter {InviterName} has no guild.", inviterName);
                client.Send(new SystemMessagePacket($"Guild not found with id {guildId}."));
                return;
            }

            // Valida coerência do guildId vindo do pacote
            if (targetGuild.Id != guildId)
            {
                _logger.Warning("GuildInviteAccept: guildId mismatch. Packet={PacketGuildId} Actual={ActualGuildId}.", guildId, targetGuild.Id);
                client.Send(new SystemMessagePacket("Guild invite no longer valid."));
                return;
            }

            // Opcional: validação básica anti-duplicados
            if (targetGuild.Members.Any(m => m.CharacterId == client.TamerId))
            {
                _logger.Information("GuildInviteAccept: Tamer {TamerId} already listed in members of guild {GuildId}.", client.TamerId, targetGuild.Id);
                client.Send(new SystemMessagePacket("You are already a member of this guild."));
                return;
            }

            _logger.Verbose("GuildInviteAccept: Tamer {InviteeId} joining guild {GuildId} ({GuildName}) via {InviterId}.",
                client.TamerId, targetGuild.Id, targetGuild.Name, inviterClient.TamerId);

            // Adiciona novo membro e sincroniza CharacterInfo do sender
            var newMember = targetGuild.AddMember(client.Tamer);

            var senderMember = targetGuild.FindMember(inviterClient.TamerId);
            if (senderMember == null)
            {
                _logger.Debug("GuildInviteAccept: inviter member record not found in guild {GuildId}, attempting to hydrate.", targetGuild.Id);
                // Cria/atualiza registro mínimo do inviter se fizer sentido na sua modelagem
                senderMember = targetGuild.AddMember(inviterClient.Tamer);
            }

            // Atualiza infos em memória (quando possível)
            senderMember?.SetCharacterInfo(inviterClient.Tamer);

            // Histórico de entrada
            var newEntry = targetGuild.AddHistoricEntry(GuildHistoricTypeEnum.MemberJoin, senderMember, newMember);

            // Liga a guild ao tamer do convidado
            client.Tamer.SetGuild(targetGuild);

            // Hidrata CharacterInfo dos membros online, evitando null-forcing
            foreach (var guildMember in targetGuild.Members)
            {
                if (guildMember.CharacterInfo == null)
                {
                    var memberClient = _mapServer.FindClientByTamerId(guildMember.CharacterId);
                    if (memberClient?.Tamer != null)
                    {
                        guildMember.SetCharacterInfo(memberClient.Tamer);
                    }
                }
            }

            // Envia info completa ao novo membro
            _logger.Debug("GuildInviteAccept: sending guild information to {TamerId}...", client.TamerId);
            client.Send(new GuildInformationPacket(targetGuild));

            // Recarrega o tamer do convidado para os outros jogadores (estado/companhia/buffs)
            try
            {
                _mapServer.BroadcastForTargetTamers(client.TamerId, new UnloadTamerPacket(client.Tamer).Serialize());
                _mapServer.BroadcastForTargetTamers(client.TamerId, new LoadTamerPacket(client.Tamer).Serialize());
                _mapServer.BroadcastForTargetTamers(client.TamerId, new LoadBuffsPacket(client.Tamer).Serialize());
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "GuildInviteAccept: broadcast reload for {TamerId} failed.", client.TamerId);
            }

            // Atualiza todos os membros da guild (online) com informações e histórico
            foreach (var guildMember in targetGuild.Members)
            {
                try
                {
                    var charId = guildMember.CharacterId;

                    // Opcional: pacote de conexão do novo membro (mantido comentado como no seu original)
                    //_mapServer.BroadcastForUniqueTamer(charId, new GuildMemberConnectPacket(client.Tamer).Finalize());

                    _logger.Debug("GuildInviteAccept: sending guild information to member {MemberId}...", charId);
                    _mapServer.BroadcastForUniqueTamer(charId, new GuildInformationPacket(targetGuild).Serialize());

                    _logger.Debug("GuildInviteAccept: sending guild historic to member {MemberId}...", charId);
                    _mapServer.BroadcastForUniqueTamer(charId, new GuildHistoricPacket(targetGuild.Historic).Serialize());
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "GuildInviteAccept: failed to send updates to member {MemberId}.", guildMember.CharacterId);
                }
            }

            // Rank atual da guild (se top 100, envia ao novo membro)
            try
            {
                _logger.Debug("GuildInviteAccept: getting current rank for guild {GuildId}...", targetGuild.Id);
                var guildRank = await _sender.Send(new GuildCurrentRankByGuildIdQuery(targetGuild.Id));
                if (guildRank > 0 && guildRank <= 100)
                {
                    _logger.Debug("GuildInviteAccept: sending guild rank to {TamerId}...", client.TamerId);
                    client.Send(new GuildRankPacket(guildRank));
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "GuildInviteAccept: failed to get/send guild rank for guild {GuildId}.", targetGuild.Id);
            }

            // Persistência (histórico + novo membro)
            try
            {
                _logger.Debug("GuildInviteAccept: saving historic entry for guild {GuildId}...", targetGuild.Id);
                await _sender.Send(new CreateGuildHistoricEntryCommand(newEntry, targetGuild.Id));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "GuildInviteAccept: failed to save historic entry for guild {GuildId}.", targetGuild.Id);
            }

            try
            {
                _logger.Debug("GuildInviteAccept: saving new member for guild {GuildId}...", targetGuild.Id);
                await _sender.Send(new CreateGuildMemberCommand(newMember, targetGuild.Id));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "GuildInviteAccept: failed to save new member for guild {GuildId}.", targetGuild.Id);
            }

            // Feedback aos dois lados (opcional, mas ajuda UX)
            try
            {
                client.Send(new SystemMessagePacket($"You have joined guild [{targetGuild.Name}]."));
                inviterClient.Send(new SystemMessagePacket($"{client.Tamer.Name} has accepted your guild invite."));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "GuildInviteAccept: failed to send confirmation messages.");
            }
        }
    }
}
