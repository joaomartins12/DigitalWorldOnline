using DigitalWorldOnline.Application;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Asset;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Models.Character;
using DigitalWorldOnline.Commons.Models.Digimon;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Packets.Items;
using DigitalWorldOnline.Game.Managers;
using DigitalWorldOnline.GameHost;
using MediatR;
using Serilog;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class QuestDeliverPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.QuestDeliver;

        private readonly StatusManager _statusManager;
        private readonly ExpManager _expManager;
        private readonly AssetsLoader _assets;
        private readonly MapServer _mapServer;
        private readonly ILogger _logger;
        private readonly ISender _sender;

        public QuestDeliverPacketProcessor(
            StatusManager statusManager,
            ExpManager expManager,
            AssetsLoader assets,
            MapServer mapServer,
            ILogger logger,
            ISender sender)
        {
            _statusManager = statusManager;
            _expManager = expManager;
            _mapServer = mapServer;
            _assets = assets;
            _logger = logger;
            _sender = sender;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);
            short questId = packet.ReadShort();

            try
            {
                // Confere se a quest existe
                var questInfo = _assets.Quest.FirstOrDefault(x => x.QuestId == questId);
                if (questInfo == null)
                {
                    _logger.Error("QuestDeliver: unknown quest id {QuestId}.", questId);
                    client.Send(new SystemMessagePacket($"Unknown quest id {questId}."));
                    client.Tamer.Progress.RemoveQuest(questId); // desfaz se estiver marcada
                    return;
                }

                // Confere se a quest está ativa no progresso do jogador
                var active = client.Tamer.Progress.InProgressQuestData.FirstOrDefault(x => x.QuestId == questId);
                if (active == null)
                {
                    client.Send(new SystemMessagePacket($"Quest {questId} is not active."));
                    return;
                }

                _logger.Verbose("Character {TamerId} delivered quest {QuestId}.", client.TamerId, questId);

                // 1) Valida e remove objetivos de coleta
                if (!TryDeliverGoalItems(client, questId, questInfo))
                    return;

                // 2) Remove suprimentos de quest entregues (se a tua lógica exigir devolução)
                if (!TryReturnSupplies(client, questId, questInfo))
                    return;

                // 3) Dá recompensas
                await GiveQuestRewards(client, questInfo);

                // 4) Desbloqueio de evolução por quest (quando não exige item de unlock)
                var evolutionQuest = _assets.EvolutionInfo.FirstOrDefault(x => x.Type == client.Partner.BaseType)?
                    .Lines.FirstOrDefault(y => y.UnlockQuestId == questId && y.UnlockItemSection == 0);

                if (evolutionQuest != null)
                {
                    var targetEvolution = client.Tamer.Partner.Evolutions[evolutionQuest.SlotLevel - 1];
                    if (targetEvolution != null)
                    {
                        targetEvolution.Unlock();
                        await _sender.Send(new UpdateEvolutionCommand(targetEvolution));

                        _logger.Verbose("Character {TamerId} unlocked evolution {Type} on quest {QuestId} completion.",
                            client.TamerId, targetEvolution.Type, questId);

                        var evoInfo = _assets.EvolutionInfo
                            .FirstOrDefault(x => x.Type == client.Partner.BaseType)?
                            .Lines.FirstOrDefault(x => x.Type == targetEvolution.Type);

                        var encyclopedia = client.Tamer.Encyclopedia.FirstOrDefault(x => x.DigimonEvolutionId == evoInfo?.EvolutionId);
                        if (encyclopedia != null)
                        {
                            var encEvo = encyclopedia.Evolutions.FirstOrDefault(x => x.DigimonBaseType == targetEvolution.Type);
                            if (encEvo != null)
                            {
                                encEvo.Unlock();
                                await _sender.Send(new UpdateCharacterEncyclopediaEvolutionsCommand(encEvo));

                                int lockedCount = encyclopedia.Evolutions.Count(x => !x.IsUnlocked);
                                if (lockedCount <= 0)
                                {
                                    encyclopedia.SetRewardAllowed();
                                    await _sender.Send(new UpdateCharacterEncyclopediaCommand(encyclopedia));
                                }
                            }
                        }
                    }
                }

                // 5) Marca como completa nos bitflags
                UpdateQuestCompleteBit(client, questId);

                // 6) Remove do progresso ativo e persiste
                Guid? removedId = client.Tamer.Progress.RemoveQuest(questId);

                // UI – inventário/recursos
                client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));

                if (removedId.HasValue)
                {
                    await _sender.Send(new RemoveActiveQuestCommand(removedId.Value));
                }

                await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
                await _sender.Send(new UpdateItemListBitsCommand(client.Tamer.Inventory.Id, client.Tamer.Inventory.Bits));
                await _sender.Send(new UpdateCharacterProgressCompleteCommand(client.Tamer.Progress));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "QuestDeliver: exception delivering quest {QuestId} (tamer {TamerId}).", questId, client?.TamerId);
                client.Send(new SystemMessagePacket($"Failed to deliver quest {questId}."));
            }
        }

        // ------------------------------------------------------------
        // Helpers de bitflag de conclusão
        // ------------------------------------------------------------
        private int GetBitValue(int[] array, int x)
        {
            int arrIDX = x / 32;
            int bitPosition = x % 32;

            if (arrIDX >= array.Length)
                throw new ArgumentOutOfRangeException(nameof(array), "Invalid array index");

            int value = array[arrIDX];
            return (value >> bitPosition) & 1;
        }

        private void SetBitValue(int[] array, int x, int bitValue)
        {
            int arrIDX = x / 32;
            int bitPosition = x % 32;

            if (arrIDX >= array.Length)
                throw new ArgumentOutOfRangeException(nameof(array), "Invalid array index");

            if (bitValue != 0 && bitValue != 1)
                throw new ArgumentException("Invalid bit value. Only 0 or 1 are allowed.", nameof(bitValue));

            int value = array[arrIDX];
            int mask = 1 << bitPosition;

            array[arrIDX] = (bitValue == 1) ? (value | mask) : (value & ~mask);
        }

        private void UpdateQuestCompleteBit(GameClient client, short questId)
        {
            int current = GetBitValue(client.Tamer.Progress.CompletedDataValue, questId - 1);
            if (current == 0)
                SetBitValue(client.Tamer.Progress.CompletedDataValue, questId - 1, 1);
        }

        // ------------------------------------------------------------
        // Remoção de Itens (objetivos / suprimentos)
        // ------------------------------------------------------------

        private bool TryDeliverGoalItems(GameClient client, short questId, QuestAssetModel questInfo)
        {
            foreach (var goal in questInfo.QuestGoals.Where(x => x.GoalType == QuestGoalTypeEnum.LootItem))
            {
                int remain = goal.GoalAmount;
                var info = _assets.ItemInfo.FirstOrDefault(x => x.ItemId == goal.GoalId);
                if (info == null)
                {
                    _logger.Error("QuestDeliver: item info not found for goal item {ItemId} (quest {QuestId}).", goal.GoalId, questId);
                    client.Send(new SystemMessagePacket($"Item information not found for item {goal.GoalId}."));
                    return false;
                }

                // Remove por pilhas até atingir o total
                while (remain > 0)
                {
                    var stack = client.Tamer.Inventory.FindItemById(goal.GoalId);
                    if (stack == null || stack.Amount <= 0)
                    {
                        client.Send(new SystemMessagePacket($"You don't have enough items for quest {questId}."));
                        return false;
                    }

                    int take = Math.Min(stack.Amount, remain);
                    client.Tamer.Inventory.RemoveOrReduceItem(stack, take, stack.Slot);
                    remain -= take;
                }

                _logger.Verbose("Character {TamerId} delivered quest {QuestId} goal item {ItemId} x{Amount}.",
                    client.TamerId, questId, goal.GoalId, goal.GoalAmount);
            }

            return true;
        }

        private bool TryReturnSupplies(GameClient client, short questId, QuestAssetModel questInfo)
        {
            foreach (var supply in questInfo.QuestSupplies)
            {
                int remain = supply.Amount;
                var info = _assets.ItemInfo.FirstOrDefault(x => x.ItemId == supply.ItemId);
                if (info == null)
                {
                    _logger.Error("QuestDeliver: item info not found for supply {ItemId} (quest {QuestId}).", supply.ItemId, questId);
                    client.Send(new SystemMessagePacket($"Item information not found for item {supply.ItemId}."));
                    return false;
                }

                // Remove por pilhas
                while (remain > 0)
                {
                    var stack = client.Tamer.Inventory.FindItemById(supply.ItemId);
                    if (stack == null || stack.Amount <= 0)
                    {
                        client.Send(new SystemMessagePacket($"You don't have the quest supply {supply.ItemId} anymore."));
                        return false;
                    }

                    int take = Math.Min(stack.Amount, remain);
                    client.Tamer.Inventory.RemoveOrReduceItem(stack, take, stack.Slot);
                    remain -= take;
                }

                _logger.Verbose("Character {TamerId} returned quest {QuestId} supply item {ItemId} x{Amount}.",
                    client.TamerId, questId, supply.ItemId, supply.Amount);
            }

            return true;
        }

        // ------------------------------------------------------------
        // Recompensas
        // ------------------------------------------------------------

        private async Task GiveQuestRewards(GameClient client, QuestAssetModel questInfo)
        {
            foreach (var reward in questInfo.QuestRewards)
            {
                switch (reward.RewardType)
                {
                    case QuestRewardTypeEnum.MoneyReward:
                        GiveMoneyReward(client, reward);
                        break;

                    case QuestRewardTypeEnum.ExperienceReward:
                        await GiveExpReward(client, reward);
                        break;

                    case QuestRewardTypeEnum.ItemReward:
                        GiveItemReward(client, reward);
                        break;
                }
            }

            // Persistir bits do jogador caso tenha havido recompensa em dinheiro
            await _sender.Send(new UpdateItemListBitsCommand(client.Tamer.Inventory.Id, client.Tamer.Inventory.Bits));
        }

        private void GiveItemReward(GameClient client, QuestRewardAssetModel reward)
        {
            foreach (var obj in reward.RewardObjectList)
            {
                var info = _assets.ItemInfo.FirstOrDefault(x => x.ItemId == obj.Reward);
                if (info == null)
                {
                    _logger.Warning("QuestDeliver: no item info for reward {ItemId}.", obj.Reward);
                    client.Send(new SystemMessagePacket($"No item info found with ID {obj.Reward}."));
                    continue;
                }

                var item = new ItemModel();
                item.SetItemInfo(info);
                item.SetItemId(obj.Reward);
                item.SetAmount(obj.Amount);

                if (item.IsTemporary)
                    item.SetRemainingTime((uint)item.ItemInfo.UsageTimeMinutes);

                var clone = (ItemModel)item.Clone();
                if (!client.Tamer.Inventory.AddItem(clone))
                {
                    client.Send(new PickItemFailPacket(PickItemFailReasonEnum.InventoryFull));
                    continue;
                }

                _logger.Verbose("Character {TamerId} received quest {QuestId} item {ItemId} x{Amount} reward.",
                    client.TamerId, reward.Quest.QuestId, obj.Reward, obj.Amount);
            }
        }

        private async Task GiveExpReward(GameClient client, QuestRewardAssetModel reward)
        {
            foreach (var obj in reward.RewardObjectList)
            {
                _logger.Verbose("Character {TamerId} received quest {QuestId} EXP reward.", client.TamerId, reward.Quest.QuestId);

                long tamerExp = obj.Amount / 10; // TODO: bônus
                var tamerRes = ReceiveTamerExp(client.Tamer, tamerExp);

                long partnerExp = obj.Amount;    // TODO: bônus
                var partnerRes = ReceivePartnerExp(client.Partner, partnerExp);

                client.Send(new ReceiveExpPacket(
                    tamerExp,
                    0,
                    client.Tamer.CurrentExperience,
                    client.Partner.GeneralHandler,
                    partnerExp,
                    0,
                    client.Partner.CurrentExperience,
                    client.Partner.CurrentEvolution.SkillExperience));

                if (tamerRes.LevelGain > 0 || partnerRes.LevelGain > 0)
                {
                    client.Send(new UpdateStatusPacket(client.Tamer));
                    _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new UpdateMovementSpeedPacket(client.Tamer).Serialize());
                }

                if (tamerRes.Success)
                {
                    await _sender.Send(new UpdateCharacterExperienceCommand(client.TamerId, client.Tamer.CurrentExperience, client.Tamer.Level));
                }

                if (partnerRes.Success)
                {
                    await _sender.Send(new UpdateDigimonExperienceCommand(client.Partner));
                }
            }
        }

        private void GiveMoneyReward(GameClient client, QuestRewardAssetModel reward)
        {
            foreach (var obj in reward.RewardObjectList)
            {
                client.Tamer.Inventory.AddBits(obj.Amount);
                _logger.Verbose("Character {TamerId} received quest {QuestId} {Amount} bits reward.",
                    client.TamerId, reward.Quest.QuestId, obj.Amount);
            }
        }

        // ------------------------------------------------------------
        // EXP helpers
        // ------------------------------------------------------------

        private ReceiveExpResult ReceiveTamerExp(CharacterModel tamer, long tamerExpToReceive)
        {
            var res = _expManager.ReceiveTamerExperience(tamerExpToReceive, tamer);

            if (res.LevelGain > 0)
            {
                _mapServer.BroadcastForTamerViewsAndSelf(tamer.Id, new LevelUpPacket(tamer.GeneralHandler, tamer.Level).Serialize());
                tamer.SetLevelStatus(_statusManager.GetTamerLevelStatus(tamer.Model, tamer.Level));
                tamer.FullHeal();
            }

            return res;
        }

        private ReceiveExpResult ReceivePartnerExp(DigimonModel partner, long partnerExpToReceive)
        {
            var res = _expManager.ReceiveDigimonExperience(partnerExpToReceive, partner);

            if (res.LevelGain > 0)
            {
                partner.SetBaseStatus(_statusManager.GetDigimonBaseStatus(partner.CurrentType, partner.Level, partner.Size));
                _mapServer.BroadcastForTamerViewsAndSelf(partner.Character.Id, new LevelUpPacket(partner.GeneralHandler, partner.Level).Serialize());
                partner.FullHeal();
            }

            return res;
        }
    }
}
