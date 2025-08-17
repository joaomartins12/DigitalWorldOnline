using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Packets.Items;
using MediatR;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class ItemRemovePacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.ItemRemove;

        private readonly ISender _sender;
        private readonly ILogger _logger;

        public ItemRemovePacketProcessor(
            ISender sender,
            ILogger logger)
        {
            _sender = sender;
            _logger = logger;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);

            var slot = packet.ReadShort();
            var posx = packet.ReadInt();   // atualmente não usado (drop desativado)
            var posy = packet.ReadInt();   // atualmente não usado (drop desativado)
            var amount = packet.ReadShort();

            // validação do item no slot
            var targetItem = client.Tamer.Inventory.FindItemBySlot(slot);
            if (targetItem == null || targetItem.ItemId <= 0)
            {
                _logger.Warning("ItemRemove: invalid slot {Slot} for tamer {TamerId}.", slot, client.TamerId);
                client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));
                return;
            }

            // normaliza amount
            if (amount <= 0)
            {
                amount = 1;
            }
            if (amount > targetItem.Amount)
            {
                amount = (short)targetItem.Amount;
            }

            try
            {
                _logger.Verbose(
                    "ItemRemove: tamer {TamerId} deleted item {ItemId} x{Amount} at map {MapId} (x:{X}, y:{Y}) [slot {Slot}].",
                    client.TamerId, targetItem.ItemId, amount, client.Tamer.Location.MapId, posx, posy, slot
                );

                // Apenas deletar do inventário (drop no chão permanece desativado)
                client.Tamer.Inventory.RemoveOrReduceItem(targetItem, amount, slot);

                // persiste alteração desse item/slot
                await _sender.Send(new UpdateItemCommand(targetItem));

                // feedback
                client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "ItemRemove: exception removing item {ItemId} for tamer {TamerId} (slot {Slot}).", targetItem.ItemId, client.TamerId, slot);
                // mesmo em erro, atualiza a UI do cliente para evitar inconsistências locais
                client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));
            }
        }
    }
}
