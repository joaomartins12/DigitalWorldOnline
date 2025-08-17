using DigitalWorldOnline.Application;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.Items;
using DigitalWorldOnline.GameHost;
using MediatR;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class EvolutionUnlockPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.EvolutionUnlock;

        private readonly AssetsLoader _assets;
        private readonly ISender _sender;
        private readonly ILogger _logger;
        private readonly MapServer _mapServer;

        public EvolutionUnlockPacketProcessor(
            AssetsLoader assets,
            ISender sender,
            ILogger logger,
            MapServer mapServer)
        {
            _assets = assets;
            _sender = sender;
            _logger = logger;
            _mapServer = mapServer;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);

            _logger.Information("Verifying EvoUnlock …");

            var evoIndex = packet.ReadInt() - 1; // 1-based -> 0-based
            var itemSlot = packet.ReadShort();

            // --- validações iniciais ---
            if (client.Partner == null || client.Partner.Evolutions == null || client.Partner.Evolutions.Count == 0)
            {
                _logger.Error("[EvoUnlock] Partner/Evolutions inválidos para Tamer {TamerId}.", client.TamerId);
                client.Send(new SystemMessagePacket("Invalid partner/evolution data."));
                return;
            }

            if (evoIndex < 0 || evoIndex >= client.Partner.Evolutions.Count)
            {
                _logger.Error("[EvoUnlock] Índice de evolução inválido {Idx} para Tamer {TamerId}.", evoIndex, client.TamerId);
                client.Send(new SystemMessagePacket("Invalid evolution index."));
                return;
            }

            var evolution = client.Partner.Evolutions[evoIndex];

            var evoInfo = _assets.EvolutionInfo
                .FirstOrDefault(x => x.Type == client.Partner.BaseType)?
                .Lines.FirstOrDefault(x => x.Type == evolution.Type);

            _logger.Information("evoIndex: {Idx}", evoIndex);
            _logger.Information("EvolutionID: {Id} | EvolutionType: {Type} | EvolutionUnlocked: {Unlocked}",
                evolution.Id, evolution.Type, evolution.Unlocked);

            if (evoInfo == null)
            {
                _logger.Error("Invalid evolution info for base type {Base} and line {Line}.",
                    client.Partner.BaseType, evolution.Type);
                client.Send(new SystemMessagePacket($"Invalid evolution info for type {client.Partner.BaseType} and line {evolution.Type}."));
                return;
            }

            // Se já estiver desbloqueada, apenas confirma ao jogador (evita gastar item por engano)
            if (evolution.Unlocked != 0) // 0 = false, 1 = true
            {
                _logger.Information("[EvoUnlock] Evolução {Type} já está desbloqueada (Tamer {TamerId}).",
                    evolution.Type, client.TamerId);
                client.Send(new SystemMessagePacket("This evolution is already unlocked."));
                return;
            }

            // ---------------------------------------------------------
            // 1) Caso Armor (usa slot <= 150): usa item específico com chance
            // ---------------------------------------------------------
            if (itemSlot <= 150)
            {
                var inventoryItem = client.Tamer.Inventory.FindItemBySlot(itemSlot);
                if (inventoryItem == null || inventoryItem.ItemInfo == null || inventoryItem.Amount <= 0)
                {
                    _logger.Warning("[EvoUnlock-Armor] Item inválido no slot {Slot} (Tamer {TamerId}).", itemSlot, client.TamerId);
                    client.Send(new SystemMessagePacket("Invalid item slot."));
                    return;
                }

                var itemInfoArmor = _assets.EvolutionsArmor.FirstOrDefault(x => x.ItemId == inventoryItem.ItemId);
                if (itemInfoArmor == null)
                {
                    _logger.Warning("[EvoUnlock-Armor] Config de armor não encontrada para ItemId {ItemId} (Tamer {TamerId}).",
                        inventoryItem.ItemId, client.TamerId);
                    client.Send(new SystemMessagePacket("Invalid armor evolution item."));
                    return;
                }

                if (inventoryItem.Amount < itemInfoArmor.Amount)
                {
                    _logger.Warning("[EvoUnlock-Armor] Quantidade insuficiente: precisa {Need}, tem {Have} (Tamer {TamerId}).",
                        itemInfoArmor.Amount, inventoryItem.Amount, client.TamerId);
                    client.Send(new SystemMessagePacket("Not enough items."));
                    return;
                }

                // Flags do pacote original
                byte success = 1; // 0 = sucesso, 1 = falha
                short result = 0; // 1 no sucesso

                // Consome
                client.Tamer.Inventory.RemoveOrReduceItem(inventoryItem, itemInfoArmor.Amount, inventoryItem.Slot);

                // Chance
                var rng = new Random();
                var roll = rng.Next(0, 100);
                var isSuccess = roll < itemInfoArmor.Chance;

                if (isSuccess)
                {
                    success = 0;
                    result = 1;

                    evolution.Unlock();

                    _logger.Verbose(
                        "Character {TamerId} unlocked ARMOR evolution {Type} for partner {PartnerId} ({Base}) using ItemId {ItemId} x{Amount}.",
                        client.TamerId, evolution.Type, client.Partner.Id, client.Partner.BaseType, itemInfoArmor.ItemId, itemInfoArmor.Amount
                    );

                    client.Send(new EvolutionArmorUnlockedPacket(result, success));

                    await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
                    await _sender.Send(new UpdateEvolutionCommand(evolution));
                }
                else
                {
                    client.Send(new EvolutionArmorUnlockedPacket(result, success));
                    await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
                }

                return;
            }

            // ---------------------------------------------------------
            // 2) Caso normal (usa Section + quantidade)
            // ---------------------------------------------------------
            var itemSection = evoInfo.UnlockItemSection;
            var requiredAmount = Math.Max(0, evoInfo.UnlockItemSectionAmount);

            if (requiredAmount == 0)
            {
                _logger.Warning("[EvoUnlock] Config com quantidade 0 para section {Section}. Desbloqueando sem custo.", itemSection);
                evolution.Unlock();
                await _sender.Send(new UpdateEvolutionCommand(evolution));
                client.Send(new SystemMessagePacket("Evolution unlocked."));
                return;
            }

            var inventoryItems = client.Tamer.Inventory.FindItemsBySection(itemSection)?.ToList() ?? new List<ItemModel>();
            if (!inventoryItems.Any())
            {
                _logger.Error("No items found with section {Section} for character {TamerId}.", itemSection, client.TamerId);
                client.Send(new SystemMessagePacket($"Invalid evolution item with section {itemSection}."));
                return;
            }

            var remaining = requiredAmount;
            var rare = false;
            var rareItemId = 0;

            foreach (var invItem in inventoryItems)
            {
                if (remaining <= 0) break;
                if (invItem.Amount <= 0) continue;

                // marca rare (se for aplicável a esse item)
                var scanAsset = _assets.ScanDetail.FirstOrDefault(s =>
                    s.Rewards != null && s.Rewards.Any(r => r.ItemId == invItem.ItemId));

                if (scanAsset != null)
                {
                    var scanReward = scanAsset.Rewards.FirstOrDefault(r => r.ItemId == invItem.ItemId);
                    if (scanReward?.Rare == true)
                    {
                        rare = true;
                        rareItemId = scanReward.ItemId;
                    }
                }

                var toConsume = Math.Min(invItem.Amount, remaining);
                client.Tamer.Inventory.RemoveOrReduceItem(invItem, toConsume, invItem.Slot);
                remaining -= toConsume;
            }

            if (remaining > 0)
            {
                // não tinha quantidade suficiente -> não desbloqueia e reverte? (no original consumia em loop até acabar)
                // Mantemos: se faltar, não desbloqueia e informa. (Itens já consumidos ficam consumidos como no original.)
                _logger.Warning("[EvoUnlock] Not enough items from section {Section}. Missing {Missing}.", itemSection, remaining);
                client.Send(new SystemMessagePacket("Not enough items to unlock evolution."));
                await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
                return;
            }

            evolution.Unlock();

            if (rare)
            {
                _mapServer.BroadcastForChannel(
                    client.Tamer.Channel,
                    new NeonMessagePacket(NeonMessageTypeEnum.Evolution, client.Tamer.Name, rareItemId, client.Tamer.Partner.CurrentType).Serialize()
                );
            }

            _logger.Verbose(
                "Character {TamerId} unlocked evolution {Type} for partner {PartnerId} ({Base}) with section {Section} x{Amount}.",
                client.TamerId, evolution.Type, client.Partner.Id, client.Partner.BaseType, itemSection, evoInfo.UnlockItemSectionAmount
            );

            await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
            await _sender.Send(new UpdateEvolutionCommand(evolution));

            _logger.Information("Evolution unlocked: ID {Id} | Type {Type} | Unlocked {Unlocked}",
                evolution.Id, evolution.Type, evolution.Unlocked);

            // ---------------- Enciclopédia ----------------
            var encyclopedia = client.Tamer.Encyclopedia.FirstOrDefault(x => x.DigimonEvolutionId == evoInfo.EvolutionId);
            if (encyclopedia != null)
            {
                var encyclopediaEvolution = encyclopedia.Evolutions.FirstOrDefault(x => x.DigimonBaseType == evolution.Type);
                if (encyclopediaEvolution != null)
                {
                    encyclopediaEvolution.Unlock();
                    await _sender.Send(new UpdateCharacterEncyclopediaEvolutionsCommand(encyclopediaEvolution));

                    var lockedCount = encyclopedia.Evolutions.Count(x => !x.IsUnlocked);
                    if (lockedCount <= 0)
                    {
                        encyclopedia.SetRewardAllowed();
                        await _sender.Send(new UpdateCharacterEncyclopediaCommand(encyclopedia));
                    }
                }
            }
        }
    }
}
