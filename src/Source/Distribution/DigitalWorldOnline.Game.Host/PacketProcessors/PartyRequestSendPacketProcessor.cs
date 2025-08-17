using System;
using System.Threading.Tasks;
using DigitalWorldOnline.Application.Separar.Queries;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.Character;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Game.Managers;
using DigitalWorldOnline.GameHost;
using MediatR;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class PartyRequestSendPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.PartyRequestSend;

        private readonly PartyManager _partyManager;
        private readonly MapServer _mapServer;
        private readonly DungeonsServer _dungeonServer;
        private readonly ILogger _logger;
        private readonly ISender _sender;

        private const int MaxNameLength = 32;
        private const int MaxPartyMembers = 4; // limite fixo

        public PartyRequestSendPacketProcessor(
            PartyManager partyManager,
            MapServer mapServer,
            DungeonsServer dungeonServer,
            ILogger logger,
            ISender sender)
        {
            _partyManager = partyManager;
            _mapServer = mapServer;
            _dungeonServer = dungeonServer;
            _logger = logger;
            _sender = sender;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            try
            {
                var packet = new GamePacketReader(packetData);
                var receiverNameRaw = packet.ReadString() ?? string.Empty;

                var receiverName = receiverNameRaw.Trim();
                if (receiverName.Length > MaxNameLength)
                    receiverName = receiverName.Substring(0, MaxNameLength);

                if (string.IsNullOrWhiteSpace(receiverName))
                {
                    client.Send(new PartyRequestSentFailedPacket(PartyRequestFailedResultEnum.Disconnected, receiverNameRaw));
                    _logger.Debug("PartyInvite: empty receiver name from TamerId={TamerId}", client.TamerId);
                    return;
                }

                // evita auto-convite
                if (receiverName.Equals(client.Tamer.Name, StringComparison.OrdinalIgnoreCase))
                {
                    client.Send(new PartyRequestSentFailedPacket(PartyRequestFailedResultEnum.CantAccept, receiverName));
                    _logger.Verbose("PartyInvite: TamerId={TamerId} tried to invite self.", client.TamerId);
                    return;
                }

                var targetCharacter = await _sender.Send(new CharacterByNameQuery(receiverName));
                if (targetCharacter == null)
                {
                    client.Send(new PartyRequestSentFailedPacket(PartyRequestFailedResultEnum.Disconnected, receiverName));
                    _logger.Verbose("PartyInvite: TamerId={TamerId} invited '{Name}' which does not exist.", client.TamerId, receiverName);
                    return;
                }

                // encontra alvo online (mapa ou dungeon)
                var targetClient =
                    _mapServer.FindClientByTamerId(targetCharacter.Id) ??
                    _dungeonServer.FindClientByTamerId(targetCharacter.Id);

                if (targetClient == null)
                {
                    client.Send(new PartyRequestSentFailedPacket(PartyRequestFailedResultEnum.Disconnected, receiverName));
                    _logger.Verbose("PartyInvite: TamerId={TamerId} invited {TargetId} but target is offline.", client.TamerId, targetCharacter.Id);
                    return;
                }

                // estados que impedem aceitar
                if (targetClient.Loading || targetClient.Tamer.State != CharacterStateEnum.Ready || targetClient.DungeonMap)
                {
                    client.Send(new PartyRequestSentFailedPacket(PartyRequestFailedResultEnum.CantAccept, receiverName));
                    _logger.Verbose("PartyInvite: Target {TargetId} cannot accept (Loading/Not Ready/In Dungeon).", targetCharacter.Id);
                    return;
                }

                // já está em party?
                var targetParty = _partyManager.FindParty(targetClient.TamerId);
                if (targetParty != null)
                {
                    client.Send(new PartyRequestSentFailedPacket(PartyRequestFailedResultEnum.AlreadyInparty, receiverName));
                    _logger.Verbose("PartyInvite: Target {TargetId} already in party {PartyId}.", targetCharacter.Id, targetParty.Id);
                    return;
                }

                // ---------- BLOCO DESCOMENTADO / ATIVADO ----------
                // Verifica party do remetente (opcional: apenas líder convida e party não pode estar cheia)
                var senderParty = _partyManager.FindParty(client.TamerId);
                if (senderParty != null)
                {
                    // precisa ser líder
                    if (senderParty.LeaderId != client.TamerId)
                    {
                        client.Send(new PartyRequestSentFailedPacket(PartyRequestFailedResultEnum.CantAccept, receiverName));
                        _logger.Verbose("PartyInvite: Non-leader {TamerId} tried to invite into PartyId={PartyId}.", client.TamerId, senderParty.Id);
                        return;
                    }

                    // party cheia (usando limite fixo)
                    if (senderParty.Members.Count >= MaxPartyMembers)
                    {
                        client.Send(new PartyRequestSentFailedPacket(PartyRequestFailedResultEnum.CantAccept, receiverName));
                        _logger.Verbose("PartyInvite: PartyId={PartyId} is full (Max={Max}).", senderParty.Id, MaxPartyMembers);
                        return;
                    }
                }
                // ---------- FIM BLOCO DESCOMENTADO ----------

                // tudo ok → envia convite
                targetClient.Send(new PartyRequestSentSuccessPacket(client.Tamer.Name));
                _logger.Verbose("PartyInvite: {From} -> {To} (id={ToId}) invitation sent.", client.Tamer.Name, receiverName, targetCharacter.Id);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "PartyInvite: exception while TamerId={TamerId} sending invite.", client.TamerId);
            }
        }
    }
}
