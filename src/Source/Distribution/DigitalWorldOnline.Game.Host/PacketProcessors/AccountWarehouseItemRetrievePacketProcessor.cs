using System;
using System.Threading.Tasks;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Packets.GameServer.Combat;
using DigitalWorldOnline.Commons.Packets.Items;
using MediatR;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class AccountWarehouseItemRetrievePacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.RetrivieAccountWarehouseItem;

        private readonly Serilog.ILogger _logger;
        private readonly ISender _sender;

        public AccountWarehouseItemRetrievePacketProcessor(Serilog.ILogger logger, ISender sender)
        {
            _logger = logger;
            _sender = sender;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            try
            {
                var packet = new GamePacketReader(packetData);
                var itemSlot = packet.ReadShort();

                if (itemSlot < 0)
                {
                    _logger.Warning("AccountWH Retrieve: invalid slot {Slot} from TamerId={TamerId}", itemSlot, client.TamerId);
                    client.Send(new SystemMessagePacket("Invalid warehouse slot."));
                    return;
                }

                var targetItem = client.Tamer.AccountCashWarehouse.FindItemBySlot(itemSlot);
                if (targetItem == null)
                {
                    _logger.Warning("AccountWH Retrieve: item not found at slot {Slot} (TamerId={TamerId})", itemSlot, client.TamerId);
                    client.Send(new SystemMessagePacket("Item not found in warehouse."));
                    return;
                }

                // Clona o item antes de mover
                var newItem = (ItemModel)targetItem.Clone();
                newItem.SetItemInfo(targetItem.ItemInfo);

                if (newItem.IsTemporary)
                {
                    var minutes = (uint)(newItem.ItemInfo?.UsageTimeMinutes ?? 0);
                    if (minutes > 0)
                        newItem.SetRemainingTime(minutes);
                }

                // Tenta adicionar no inventário do jogador
                var added = client.Tamer.Inventory.AddItem(newItem);
                if (!added)
                {
                    _logger.Information("AccountWH Retrieve: inventory full or cannot stack (TamerId={TamerId}, ItemId={ItemId})",
                        client.TamerId, newItem.ItemId);
                    client.Send(new SystemMessagePacket("Not enough space in inventory."));
                    // Recarrega UI para garantir sincronismo
                    client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));
                    client.Send(new LoadAccountWarehousePacket(client.Tamer.AccountCashWarehouse));
                    return;
                }

                // Remover do armazém (total do slot)
                client.Tamer.AccountCashWarehouse.RemoveOrReduceItem(targetItem, targetItem.Amount);
                client.Tamer.AccountCashWarehouse.Sort();

                // Notificar cliente
                client.Send(new AccountWarehouseItemRetrievePacket(newItem, itemSlot));
                client.Send(new LoadAccountWarehousePacket(client.Tamer.AccountCashWarehouse));
                client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));

                // Persistência
                await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
                await _sender.Send(new UpdateItemsCommand(client.Tamer.AccountCashWarehouse));

                _logger.Information("AccountWH Retrieve: OK TamerId={TamerId} ItemId={ItemId} Amount={Amount} Slot={Slot}",
                    client.TamerId, newItem.ItemId, newItem.Amount, itemSlot);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "AccountWH Retrieve: exception for TamerId={TamerId}", client.TamerId);
                client.Send(new SystemMessagePacket("An error occurred while retrieving the item."));
            }
        }
    }
}
