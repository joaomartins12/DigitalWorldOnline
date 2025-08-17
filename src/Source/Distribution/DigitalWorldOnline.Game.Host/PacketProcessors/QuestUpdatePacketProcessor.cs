using System;
using System.Linq;
using DigitalWorldOnline.Application;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Packets.Items;
using MediatR;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class QuestUpdatePacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.QuestUpdate;

        private readonly AssetsLoader _assets;
        private readonly ILogger _logger;
        private readonly ISender _sender;

        public QuestUpdatePacketProcessor(
            AssetsLoader assets,
            ILogger logger,
            ISender sender)
        {
            _assets = assets;
            _logger = logger;
            _sender = sender;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);

            short questId = packet.ReadShort();
            byte goalIndex = packet.ReadByte();

            try
            {
                var questInfo = _assets.Quest.FirstOrDefault(x => x.QuestId == questId);
                if (questInfo == null)
                {
                    _logger.Error("QuestUpdate: unknown quest id {QuestId}.", questId);
                    client.Send(new SystemMessagePacket($"Unknown quest id {questId}."));
                    return;
                }

                // Verifica índice da goal
                if (goalIndex < 0 || goalIndex >= questInfo.QuestGoals.Count)
                {
                    _logger.Warning("QuestUpdate: invalid goal index {GoalIndex} for quest {QuestId}.", goalIndex, questId);
                    client.Send(new SystemMessagePacket($"Invalid quest goal index {goalIndex} for quest {questId}."));
                    return;
                }

                var targetGoal = questInfo.QuestGoals[goalIndex];

                // Garante que a quest está ativa para este jogador
                var questInProgress = client.Tamer.Progress.InProgressQuestData.FirstOrDefault(x => x.QuestId == questId);
                if (questInProgress == null)
                {
                    _logger.Warning("QuestUpdate: quest {QuestId} is not active for tamer {TamerId}.", questId, client.TamerId);
                    return;
                }

                // Valor atual e limite
                int currentValue = client.Tamer.Progress.GetQuestGoalProgress(questId, goalIndex);
                int maxValue = Math.Max(1, targetGoal.GoalAmount);

                // Para objetivos que exigem consumo de item, consome antes de atualizar o progresso
                bool shouldIncrement = true;

                switch (targetGoal.GoalType)
                {
                    case QuestGoalTypeEnum.UseItem:
                    case QuestGoalTypeEnum.UseItemInNpc:
                    case QuestGoalTypeEnum.UseItemAtRegion:
                    case QuestGoalTypeEnum.UseItemInMonster:
                        {
                            // Se já completou, não precisa consumir mais
                            if (currentValue >= maxValue)
                            {
                                shouldIncrement = false;
                                break;
                            }

                            var invItem = client.Tamer.Inventory.FindItemById(targetGoal.GoalId);
                            if (invItem == null || invItem.Amount < 1)
                            {
                                shouldIncrement = false;
                                client.Send(new SystemMessagePacket($"Required item {targetGoal.GoalId} not found."));
                                _logger.Verbose("QuestUpdate: missing item {ItemId} for quest {QuestId} goal {GoalIndex} (tamer {TamerId}).",
                                    targetGoal.GoalId, questId, goalIndex, client.TamerId);
                                break;
                            }

                            // Remove exatamente 1 do slot correto
                            client.Tamer.Inventory.RemoveOrReduceItem(invItem, 1, invItem.Slot);
                            await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
                            client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));
                            break;
                        }

                    case QuestGoalTypeEnum.ReachRegion:
                    case QuestGoalTypeEnum.TalkToNpc:
                        // apenas incrementa
                        break;

                    case QuestGoalTypeEnum.ClientAction:
                    case QuestGoalTypeEnum.ReachLevel:
                    case QuestGoalTypeEnum.AcquirePartner:
                    default:
                        _logger.Warning("QuestUpdate: goal type {Type} not implemented for quest {QuestId} goal {GoalIndex}.",
                            targetGoal.GoalType, questId, goalIndex);
                        client.Send(new SystemMessagePacket($"Quest {questId} goal {goalIndex} not implemented."));
                        return;
                }

                if (!shouldIncrement)
                {
                    // Nada para atualizar (ou já completo, ou faltou item)
                    return;
                }

                // Atualiza progresso (cap no máximo)
                int newValue = Math.Min(currentValue + 1, maxValue);

                // As APIs esperam byte para o progresso
                byte newValueByte = (byte)Math.Min(newValue, byte.MaxValue);

                // Persiste no progresso em memória
                client.Tamer.Progress.UpdateQuestInProgress(questId, goalIndex, newValueByte);

                // Notifica cliente do novo valor
                client.Send(new QuestGoalUpdatePacket(questId, goalIndex, newValueByte));

                _logger.Verbose("QuestUpdate: tamer {TamerId} updated quest {QuestId} goal {GoalIndex} -> {NewValue}/{Max}.",
                    client.TamerId, questId, goalIndex, newValueByte, maxValue);

                // Persiste o progresso da quest ativa
                await _sender.Send(new UpdateCharacterInProgressCommand(questInProgress));

            }
            catch (Exception ex)
            {
                _logger.Error(ex, "QuestUpdate: exception while updating quest {QuestId} (tamer {TamerId}).",
                    questId, client?.TamerId);
                client.Send(new SystemMessagePacket("Failed to update quest progress due to an internal error."));
            }
        }
    }
}
