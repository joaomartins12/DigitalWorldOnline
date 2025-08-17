using AutoMapper;
using DigitalWorldOnline.Application;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Application.Separar.Queries;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Asset;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.Items;
using DigitalWorldOnline.Commons.Utils;
using MediatR;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class ItemCraftPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.ItemCraft;

        private readonly AssetsLoader _assets;
        private readonly ISender _sender;
        private readonly ILogger _logger;
        private readonly IMapper _mapper;

        public ItemCraftPacketProcessor(
            AssetsLoader assets,
            ISender sender,
            ILogger logger,
            IMapper mapper)
        {
            _assets = assets;
            _sender = sender;
            _logger = logger;
            _mapper = mapper;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);

            var npcId = packet.ReadInt();
            var sequencialId = packet.ReadInt();
            var requestAmount = packet.ReadInt();

            // ainda não utilizados pelo cliente
            var increaseRateItem = packet.ReadInt();
            var protectItem = packet.ReadInt();

            if (requestAmount <= 0)
            {
                client.Send(new SystemMessagePacket("Invalid craft amount."));
                _logger.Warning("ItemCraft: invalid requestAmount {Amount} (tamer {TamerId}).", requestAmount, client.TamerId);
                return;
            }

            var craftRecipe = _mapper.Map<ItemCraftAssetModel>(
                await _sender.Send(new ItemCraftAssetsByFilterQuery(npcId, sequencialId))
            );

            if (craftRecipe == null)
            {
                client.Send(new SystemMessagePacket($"Item craft not found with NPC id {npcId} and id {sequencialId}."));
                _logger.Warning("Item craft not found with NPC id {NpcId} and id {SeqId} (tamer {TamerId}).",
                    npcId, sequencialId, client.TamerId);
                return;
            }

            // item resultante da receita
            var craftedItemProto = new ItemModel(craftRecipe.ItemId, craftRecipe.Amount);
            craftedItemProto.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == craftRecipe.ItemId));

            if (craftedItemProto.ItemInfo == null)
            {
                client.Send(new SystemMessagePacket($"Invalid crafted item info for item id {craftRecipe.ItemId}."));
                _logger.Warning("ItemCraft: invalid crafted item info {ItemId} (tamer {TamerId}).",
                    craftRecipe.ItemId, client.TamerId);
                return;
            }

            // prepara tempo padrão se for temporário
            craftedItemProto.SetDefaultRemainingTime();

            var totalCrafted = 0;
            var triesLeft = requestAmount;

            while (triesLeft > 0)
            {
                // 1) remover materiais desta tentativa (com rollback local se faltar)
                var removedThisTry = new List<ItemModel>();
                bool materialsOk = true;

                foreach (var material in craftRecipe.Materials)
                {
                    var needed = new ItemModel(material.ItemId, material.Amount);
                    if (!client.Tamer.Inventory.RemoveOrReduceItemWithoutSlot(needed))
                    {
                        materialsOk = false;
                        break;
                    }

                    removedThisTry.Add(needed);
                }

                if (!materialsOk)
                {
                    // devolve materiais desta tentativa
                    foreach (var m in removedThisTry)
                        client.Tamer.Inventory.AddItem(new ItemModel(m.ItemId, m.Amount));

                    client.Send(new SystemMessagePacket("Insufficient materials to continue crafting."));
                    _logger.Verbose("ItemCraft: insufficient materials on try {Try}, stopped early (tamer {TamerId}).",
                        (requestAmount - triesLeft + 1), client.TamerId);
                    break;
                }

                // 2) remover bits desta tentativa (com rollback de materiais em caso de falha)
                if (!client.Tamer.Inventory.RemoveBits(craftRecipe.Price))
                {
                    foreach (var m in removedThisTry)
                        client.Tamer.Inventory.AddItem(new ItemModel(m.ItemId, m.Amount));

                    client.Send(new SystemMessagePacket("Insufficient bits to continue crafting."));
                    _logger.Verbose("ItemCraft: insufficient bits on try {Try}, stopped early (tamer {TamerId}).",
                        (requestAmount - triesLeft + 1), client.TamerId);
                    break;
                }

                // 3) rolar chance
                var success = craftRecipe.SuccessRate >= UtilitiesFunctions.RandomByte(maxValue: 100);
                if (success)
                {
                    totalCrafted++;
                }

                triesLeft--;
            }

            // adicionar ao inventário os itens craftados
            if (totalCrafted > 0)
            {
                for (int i = 0; i < totalCrafted; i++)
                {
                    var temp = (ItemModel)craftedItemProto.Clone();
                    var added = client.Tamer.Inventory.AddItem(temp);
                    if (!added)
                    {
                        client.Send(new SystemMessagePacket("Inventory full while adding crafted items."));
                        _logger.Warning("ItemCraft: inventory full when adding crafted output (tamer {TamerId}).", client.TamerId);
                        break;
                    }
                }
            }

            var attemptsDone = requestAmount - triesLeft;
            var materialList = string.Join(',', craftRecipe.Materials.Select(x => $"{x.ItemId} x{x.Amount}"));
            var bitsSpent = attemptsDone * craftRecipe.Price;

            _logger.Verbose(
                "Character {TamerId} attempted craft {ItemId} {Times}x (success {Success}x) with {Materials} and {Bits} bits.",
                client.TamerId, craftRecipe.ItemId, attemptsDone, totalCrafted, materialList, bitsSpent
            );

            // persistências finais
            await _sender.Send(new UpdateItemListBitsCommand(client.Tamer.Inventory));
            await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));

            // feedback ao cliente
            client.Send(
                UtilitiesFunctions.GroupPackets(
                    new CraftItemPacket(craftRecipe, attemptsDone, totalCrafted).Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                )
            );
        }
    }
}
