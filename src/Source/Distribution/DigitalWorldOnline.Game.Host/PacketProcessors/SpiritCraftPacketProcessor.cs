﻿using DigitalWorldOnline.Application;
using DigitalWorldOnline.Application.Separar.Commands.Delete;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Packets.Items;
using DigitalWorldOnline.Commons.Packets.Chat;
using MediatR;
using Serilog;
using DigitalWorldOnline.Commons.Models.Asset;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class SpiritCraftPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.SpiritCraft;

        private readonly ILogger _logger;
        private readonly ISender _sender;
        private readonly AssetsLoader _assets;

        public SpiritCraftPacketProcessor(ILogger logger, ISender sender, AssetsLoader assets)
        {
            _logger = logger;
            _sender = sender;
            _assets = assets;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);

            var slot = packet.ReadByte();
            var validation = packet.ReadString();
            var x = packet.ReadByte();
            var npcId = packet.ReadInt();

            var digimonId = client.Tamer.Digimons.First(x => x.Slot == slot).Id;
            var targetType = client.Tamer.Digimons.First(x => x.Slot == slot).BaseType;

            var result = client.PartnerDeleteValidation(validation);

            var extraEvolutionNpc = _assets.ExtraEvolutions.FirstOrDefault(x => x.NpcId == npcId);

            if (extraEvolutionNpc == null)
            {
                _logger.Warning($"Extra Evolution NPC not found for Item Craft !!");
                return;
            }

            var extraEvolutionInfo = extraEvolutionNpc.ExtraEvolutionInformation.FirstOrDefault(x => x.ExtraEvolution.Any(extra => extra.Requireds.Any(required => required.ItemId == targetType)))?.ExtraEvolution;

            if (extraEvolutionInfo == null)
            {
                _logger.Warning($"Extra Evolution information not found for Item Craft !!");
                return;
            }

            var extraEvolution = extraEvolutionInfo.FirstOrDefault(x => x.Requireds.Any(x => x.ItemId == targetType));

            if (extraEvolution == null)
            {
                _logger.Warning($"Extra Evolution Item not found for Item Craft !!");
                return;
            }

            if (result > 0)
            {
                // ------------------------------------------------------------------------

                if (!client.Tamer.Inventory.RemoveBits(extraEvolution.Price))
                {
                    client.Send(new SystemMessagePacket($"Insuficient bits for item craft."));
                    _logger.Warning($"Insuficient bits for item craft NPC id {npcId} for tamer {client.TamerId}.");
                    return;
                }

                var materialToPacket = new List<ExtraEvolutionMaterialAssetModel>();
                var requiredsToPacket = new List<ExtraEvolutionRequiredAssetModel>();

                foreach (var material in extraEvolution.Materials)
                {
                    var itemToRemove = client.Tamer.Inventory.FindItemById(material.ItemId);

                    if (itemToRemove != null)
                    {
                        materialToPacket.Add(material);
                        client.Tamer.Inventory.RemoveOrReduceItemWithoutSlot(new ItemModel(material.ItemId, material.Amount));

                        break;
                    }
                }

                foreach (var material in extraEvolution.Requireds)
                {
                    var itemToRemove = client.Tamer.Inventory.FindItemById(material.ItemId);

                    if (itemToRemove != null)
                    {
                        requiredsToPacket.Add(material);
                        client.Tamer.Inventory.RemoveOrReduceItemWithoutSlot(new ItemModel(material.ItemId, material.Amount));

                        if (extraEvolution.Requireds.Count <= 3)
                        {
                            break;
                        }
                    }
                }

                // ------------------------------------------------------------------------

                var craftedItem = new ItemModel(extraEvolution.DigimonId, 1);
                craftedItem.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == craftedItem.ItemId));

                var tempItem = (ItemModel)craftedItem.Clone();

                client.Tamer.Inventory.AddItem(tempItem);

                client.Tamer.RemoveDigimon(slot);

                client.Send(new SpiritCraftPacket(slot, (int)extraEvolution.Price, extraEvolution.DigimonId));
                client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));

                await _sender.Send(new DeleteDigimonCommand(digimonId));
                await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
                await _sender.Send(new UpdateItemListBitsCommand(client.Tamer.Inventory.Id, client.Tamer.Inventory.Bits));

                _logger.Verbose($"Character {client.TamerId} deleted partner {digimonId}.");
            }
            else
            {
                client.Send(new PartnerDeletePacket(result));
                _logger.Verbose($"Character {client.TamerId} failed to deleted partner {digimonId} with invalid account information.");
            }
        }
    }
}