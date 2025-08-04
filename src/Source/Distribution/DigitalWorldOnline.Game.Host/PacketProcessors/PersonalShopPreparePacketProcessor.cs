using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Packets.Items;
using DigitalWorldOnline.Commons.Packets.PersonalShop;
using DigitalWorldOnline.GameHost;

using MediatR;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class PersonalShopPreparePacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.PersonalShopPrepare;

        private readonly MapServer _mapServer;
        private readonly ILogger _logger;
        private readonly ISender _sender;

        public PersonalShopPreparePacketProcessor(
            MapServer mapServer,
            ILogger logger,
            ISender sender)
        {
            _mapServer = mapServer;
            _logger = logger;
            _sender = sender;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);

            //_logger.Information($"--- Personal/Consignment Shop Prepare Packet 1510 ---\n");

            var requestType = (TamerShopActionEnum)packet.ReadInt();
            var itemSlot = packet.ReadShort();

            if (requestType == TamerShopActionEnum.TamerShopRequest || requestType == TamerShopActionEnum.ConsignedShopRequest)
            {
                var itemUsed = client.Tamer.Inventory.FindItemBySlot(itemSlot);

                if (itemUsed.ItemInfo?.Section != 7601)
                {
                    if (client.Tamer.ConsignedShop != null)
                    {
                        client.Send(new PersonalShopPacket(TamerShopActionEnum.TamerShopRequest, 0));
                        return;
                    }
                }
            }

            var itemId = 0;
            var action = TamerShopActionEnum.CloseWindow;

            switch (requestType)
            {
                case TamerShopActionEnum.TamerShopRequest:
                case TamerShopActionEnum.TamerShopWithItensCloseRequest:
                    action = TamerShopActionEnum.TamerShopWindow;
                    break;
                case TamerShopActionEnum.ConsignedShopRequest:
                    action = TamerShopActionEnum.ConsignedShopWindow;
                    break;
                case TamerShopActionEnum.CloseShopRequest:
                case TamerShopActionEnum.TamerShopWithoutItensCloseRequest:
                    action = TamerShopActionEnum.CloseWindow;
                    break;
            }

            if (itemSlot <= GeneralSizeEnum.InventoryMaxSlot.GetHashCode())
            {
                itemId = client.Tamer.Inventory.Items[itemSlot]?.ItemId ?? -1;
                _logger.Debug($"Shop item id {itemId}");

                if (itemId == 0) return;
            }

            client.Tamer.UpdateShopItemId(itemId);

            _logger.Debug($"Sending sync condition packet...");

            if (requestType == TamerShopActionEnum.CloseShopRequest)
                client.Tamer.RestorePreviousCondition();
            else
                client.Tamer.UpdateCurrentCondition(ConditionEnum.PreparingShop);

            if (requestType == TamerShopActionEnum.TamerShopWithoutItensCloseRequest)
                client.Tamer.UpdateCurrentCondition(ConditionEnum.Default);

            _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new SyncConditionPacket(client.Tamer.GeneralHandler, client.Tamer.CurrentCondition).Serialize());

            _logger.Debug($"Sending personal shop packet with action {action}...");

            if (requestType == TamerShopActionEnum.TamerShopWithItensCloseRequest)
            {
                client.Tamer.UpdateCurrentCondition(ConditionEnum.Default);
                
                client.Tamer.Inventory.AddItems(client.Tamer.TamerShop.Items);
                client.Tamer.TamerShop.Clear();

                client.Send(new PersonalShopPacket());

                await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
                await _sender.Send(new UpdateItemsCommand(client.Tamer.TamerShop)); 
                
                client.Send(new LoadInventoryPacket(client.Tamer.Inventory,
                InventoryTypeEnum.Inventory)
                .Serialize());

                _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new SyncConditionPacket(client.Tamer.GeneralHandler, client.Tamer.CurrentCondition).Serialize());
            }
            else if (requestType == TamerShopActionEnum.TamerShopWithoutItensCloseRequest)
            {
                client.Send(new PersonalShopPacket());
            }
            else
            {
                client.Send(new PersonalShopPacket(action, client.Tamer.ShopItemId));
            }
        }
    }
}