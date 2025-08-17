using System;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using MediatR;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class QuestGiveUpPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.QuestGiveUp;

        private readonly ILogger _logger;
        private readonly ISender _sender;

        public QuestGiveUpPacketProcessor(
            ILogger logger,
            ISender sender)
        {
            _logger = logger;
            _sender = sender;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);
            short questId = packet.ReadShort();

            try
            {
                _logger.Verbose("QuestGiveUp: Tamer {TamerId} solicitou desistir da quest {QuestId}.",
                    client?.TamerId, questId);

                // Remove do progresso do jogador; retorna o ID do registro ativo (Guid?) se existir
                Guid? removedId = client?.Tamer?.Progress?.RemoveQuest(questId);

                if (removedId.HasValue)
                {
                    await _sender.Send(new RemoveActiveQuestCommand(removedId.Value));
                    _logger.Verbose("QuestGiveUp: quest {QuestId} removida (activeId={ActiveId}) para tamer {TamerId}.",
                        questId, removedId.Value, client?.TamerId);
                }
                else
                {
                    _logger.Warning("QuestGiveUp: quest {QuestId} não estava ativa para tamer {TamerId}. Nada a remover.",
                        questId, client?.TamerId);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "QuestGiveUp: exceção ao desistir da quest {QuestId} (tamer {TamerId}).",
                    questId, client?.TamerId);
                // Mantemos silencioso no cliente para não poluir o chat; servidor já loga o problema.
            }
        }
    }
}
