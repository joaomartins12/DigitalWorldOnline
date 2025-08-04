using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.Character;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.GameHost;
using Serilog;
using System.Net.Sockets;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class TradeAddItemPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.TradeAddItem;

        private readonly MapServer _mapServer;
        private readonly ILogger _logger;


        public TradeAddItemPacketProcessor(
            MapServer mapServer,
            ILogger logger)
        {
            _mapServer = mapServer;
            _logger = logger;

        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);
            var inventorySlot = packet.ReadShort();
            var amount = packet.ReadShort();
            var slotAtual = client.Tamer.TradeInventory.EquippedItems.Count;

            var targetClient = _mapServer.FindClientByTamerHandleAndChannel(client.Tamer.TargetTradeGeneralHandle, client.TamerId);
            var Item = client.Tamer.Inventory.FindItemBySlot(inventorySlot);


            if (client.Tamer.Inventory.CountItensById(Item.ItemId) < amount)
            {
                await _mapServer.CallDiscord($"try DUPPING in TRADE {amount}x {Item.ItemInfo.Name}, but he has {Item.Amount}x!", client, "e06666", "DISCONNECTED", "1280704020469514291");
                client.Disconnect();
                return;
            }

            var firstTamerItems = client.Tamer.TradeInventory.EquippedItems.Select(x => $"{x.ItemId} x{x.Amount}");
            var secondTamerItems = targetClient.Tamer.TradeInventory.EquippedItems.Select(x => $"{x.ItemId} x{x.Amount}");

            var EmptSlot = client.Tamer.TradeInventory.GetEmptySlot;

            var NewItem = (ItemModel)Item.Clone();
            NewItem.Amount = amount;


            foreach (var item in client.Tamer.TradeInventory.Items)
            {
                if (EmptSlot != -1)
                {
                    client.Tamer.TradeInventory.AddItemTrade(NewItem);
                    break;
                }
            }

            client.Send(new TradeAddItemPacket(client.Tamer.GeneralHandler, NewItem.ToArray(), (byte)EmptSlot, inventorySlot));
            targetClient.Send(new TradeAddItemPacket(client.Tamer.GeneralHandler, NewItem.ToArray(), (byte)EmptSlot, inventorySlot));

            targetClient.Send(new TradeInventoryUnlockPacket(client.Tamer.TargetTradeGeneralHandle));
            client.Send(new TradeInventoryUnlockPacket(client.Tamer.TargetTradeGeneralHandle));
        }
    }
}

