using DigitalWorldOnline.Application;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Packets.Items;
using DigitalWorldOnline.Commons.Utils;
using DigitalWorldOnline.GameHost;
using MediatR;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class ItemScanPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.ItemScan;

        private readonly AssetsLoader _assets;
        private readonly MapServer _mapServer;
        private readonly ISender _sender;
        private readonly ILogger _logger;

        public ItemScanPacketProcessor(
            AssetsLoader assets,
            MapServer mapServer,
            ISender sender,
            ILogger logger)
        {
            _assets = assets;
            _mapServer = mapServer;
            _sender = sender;
            _logger = logger;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);

            var vipEnabled = packet.ReadByte();
            var _ = packet.ReadInt(); // valor ignorado (u2)
            var npcId = packet.ReadInt();
            var slotToScan = packet.ReadInt();
            var amountToScan = packet.ReadShort();

            var scannedItem = client.Tamer.Inventory.FindItemBySlot(slotToScan);
            if (scannedItem == null || scannedItem.ItemId == 0 || scannedItem.ItemInfo == null)
            {
                client.Send(new SystemMessagePacket($"Invalid item at slot {slotToScan}."));
                _logger.Warning($"Invalid item on slot {slotToScan} for tamer {client.TamerId} on scanning.");
                return;
            }

            if (client.Tamer.Inventory.CountItensById(scannedItem.ItemId) < amountToScan)
            {
                await _mapServer.CallDiscord(
                    $"try DUPPING in SCANN {amountToScan}x {scannedItem.ItemInfo.Name}, but he has {scannedItem.Amount}x!",
                    client, "e06666", "DISCONNECTED", "1280704020469514291");
                client.Disconnect();
                return;
            }

            var scanAsset = _assets.ScanDetail.FirstOrDefault(x => x.ItemId == scannedItem.ItemId);
            if (scanAsset == null)
            {
                client.Send(
                    UtilitiesFunctions.GroupPackets(
                        new SystemMessagePacket($"No scan configuration for item id {scannedItem.ItemId}.").Serialize(),
                        new ItemScanFailPacket(client.Tamer.Inventory.Bits, slotToScan, scannedItem.ItemId).Serialize(),
                        new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                    )
                );
                _logger.Warning($"No scan configuration for item id {scannedItem.ItemId}");
                return;
            }

            var receivedRewards = new Dictionary<int, ItemModel>();
            short scannedItens = 0;
            long cost = 0;
            var error = false;

            while (scannedItens < amountToScan && !error)
            {
                if (!scanAsset.Rewards.Any())
                {
                    _logger.Warning($"Scan config for item {scanAsset.ItemId} has incorrect rewards configuration.");
                    client.Send(new SystemMessagePacket($"Scan config for item {scanAsset.ItemId} has incorrect rewards configuration."));
                    break;
                }

                var possibleRewards = scanAsset.Rewards.OrderBy(x => Guid.NewGuid()).ToList();
                foreach (var possibleReward in possibleRewards)
                {
                    if (cost + scannedItem.ItemInfo.ScanPrice > client.Tamer.Inventory.Bits)
                    {
                        _logger.Warning($"No more bits after start scanning for tamer {client.TamerId}.");
                        error = true;
                        break;
                    }

                    if (possibleReward.Chance >= UtilitiesFunctions.RandomDouble())
                    {
                        var itemRewardAmount = UtilitiesFunctions.RandomInt(possibleReward.MinAmount, possibleReward.MaxAmount);

                        var contentItem = new ItemModel();
                        contentItem.SetItemId(possibleReward.ItemId);
                        contentItem.SetAmount(itemRewardAmount);
                        contentItem.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == possibleReward.ItemId));

                        if (contentItem.ItemInfo == null)
                        {
                            _logger.Warning($"Invalid item info for item {possibleReward.ItemId} in tamer {client.TamerId} scan.");
                            client.Send(new SystemMessagePacket($"Invalid item info for item {possibleReward.ItemId}."));
                            error = true;
                            break;
                        }

                        // Aplicar valores de Chipset se necessário
                        if (contentItem.ItemInfo.Section == 5200)
                        {
                            ApplyValuesChipset(contentItem);
                        }

                        if (contentItem.IsTemporary)
                            contentItem.SetRemainingTime((uint)contentItem.ItemInfo.UsageTimeMinutes);

                        var targetSlot = client.Tamer.Inventory.FindAvailableSlot(contentItem);

                        if (targetSlot != client.Tamer.Inventory.GetEmptySlot)
                        {
                            var inventoryItem = client.Tamer.Inventory.FindItemBySlot(targetSlot);
                            var tempItem = (ItemModel)inventoryItem.Clone();
                            tempItem.IncreaseAmount(contentItem.Amount);

                            if (!receivedRewards.ContainsKey(targetSlot))
                                receivedRewards.Add(targetSlot, tempItem);
                            else
                                receivedRewards[targetSlot].IncreaseAmount(contentItem.Amount);
                        }
                        else
                        {
                            var tempItem = (ItemModel)contentItem.Clone();

                            if (!receivedRewards.ContainsKey(targetSlot))
                                receivedRewards.Add(targetSlot, tempItem);
                            else
                                receivedRewards[targetSlot].IncreaseAmount(contentItem.Amount);
                        }

                        if (client.Tamer.Inventory.AddItem(contentItem))
                        {
                            if (possibleReward.Rare)
                                _mapServer.BroadcastForChannel(
                                    client.Tamer.Channel,
                                    new NeonMessagePacket(NeonMessageTypeEnum.Item, client.Tamer.Name, scanAsset.ItemId, possibleReward.ItemId).Serialize()
                                );

                            cost += scannedItem.ItemInfo.ScanPrice;
                            scannedItens++;
                        }
                        else
                        {
                            _logger.Warning($"No more space after start scanning for tamer {client.TamerId}.");
                            error = true;
                            break;
                        }
                    }

                    if (scannedItens >= amountToScan || error)
                        break;
                }
            }

            // Envia o pacote de sucesso
            client.Send(new ItemScanSuccessPacket(
                cost,
                client.Tamer.Inventory.Bits - cost,
                slotToScan,
                scannedItem.ItemId,
                scannedItens,
                receivedRewards));

            var dropList = string.Join(',', receivedRewards.Select(x => $"{x.Value.ItemId} x{x.Value.Amount}"));

            if (vipEnabled == 1)
                _logger.Verbose($"Character {client.TamerId} scanned {scannedItem.ItemId} x{scannedItens} with VIP and obtained {dropList}");
            else
                _logger.Verbose($"Character {client.TamerId} scanned {scannedItem.ItemId} x{scannedItens} at {client.TamerLocation} with NPC {npcId} and obtained {dropList}");

            // Remove custos e itens escaneados
            client.Tamer.Inventory.RemoveBits(cost);
            client.Tamer.Inventory.RemoveOrReduceItem(scannedItem, scannedItens, slotToScan);

            // Atualiza progresso de quest
            var scanQuest = client.Tamer.Progress.InProgressQuestData.FirstOrDefault(x => x.QuestId == 4021);
            if (scanQuest != null && scanAsset.ItemId == 9071)
            {
                scanQuest.UpdateCondition(0, 1);
                client.Send(new QuestGoalUpdatePacket(4021, 0, 1));
                var questToUpdate = client.Tamer.Progress.InProgressQuestData.FirstOrDefault(x => x.QuestId == 4021);
                if (questToUpdate != null)
                    await _sender.Send(new UpdateCharacterInProgressCommand(questToUpdate));
            }

            // Atualiza inventário e bits no banco
            await _sender.Send(new UpdateItemListBitsCommand(client.Tamer.Inventory));
            await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
        }

        private ItemModel ApplyValuesChipset(ItemModel newItem)
        {
            var chipsetInfo = _assets.SkillCodeInfo
                .FirstOrDefault(x => x.SkillCode == newItem.ItemInfo.SkillCode)?
                .Apply.FirstOrDefault(x => x.Type > 0);

            var chipsetSkill = _assets.SkillInfo
                .FirstOrDefault(x => x.SkillId == newItem.ItemInfo.SkillCode)?.FamilyType ?? 0;

            if (chipsetInfo == null)
                return newItem;

            var random = new Random();
            int applyRate = random.Next(newItem.ItemInfo.ApplyValueMin, newItem.ItemInfo.ApplyValueMax);
            var nValue = chipsetInfo.Value + (newItem.ItemInfo.TypeN) * chipsetInfo.AdditionalValue;

            int valorAleatorio = (int)((double)applyRate * nValue / 100);

            newItem.AccessoryStatus = newItem.AccessoryStatus.OrderBy(x => x.Slot).ToList();
            var possibleStatus = (AccessoryStatusTypeEnum)chipsetInfo.Attribute;

            newItem.AccessoryStatus[0].SetType(possibleStatus);
            newItem.AccessoryStatus[0].SetValue((short)valorAleatorio);

            newItem.SetPower((byte)applyRate);
            newItem.SetReroll(100);
            newItem.SetFamilyType(chipsetSkill);

            return newItem;
        }
    }
}
