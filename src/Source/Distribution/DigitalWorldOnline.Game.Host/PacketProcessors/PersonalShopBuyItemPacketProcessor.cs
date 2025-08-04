﻿using AutoMapper;
using DigitalWorldOnline.Application;
using DigitalWorldOnline.Application.Separar.Commands.Delete;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Application.Separar.Queries;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Extensions;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Models.Character;
using DigitalWorldOnline.Commons.Models.TamerShop;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Packets.Items;
using DigitalWorldOnline.Commons.Packets.PersonalShop;
using DigitalWorldOnline.GameHost;
using MediatR;
using Newtonsoft.Json.Linq;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class PersonalShopPurchaseItemPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.TamerShopBuy;

        private readonly AssetsLoader _assets;
        private readonly MapServer _mapServer;
        private readonly ILogger _logger;
        private readonly IMapper _mapper;
        private readonly ISender _sender;
        private bool hasItem = false;

        public PersonalShopPurchaseItemPacketProcessor(
            MapServer mapServer,
            AssetsLoader assets,
            ILogger logger,
            IMapper mapper,
            ISender sender)
        {
            _mapServer = mapServer;
            _assets = assets;
            _logger = logger;
            _mapper = mapper;
            _sender = sender;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);

            _logger.Information($"PersonalShopBuy");

            var shopHandler = packet.ReadInt();
            var shopSlot = packet.ReadInt();
            var boughtItemId = packet.ReadInt();
            var boughtAmount = packet.ReadInt();
            packet.Skip(60);
            var boughtUnitPrice = packet.ReadInt64();

            _logger.Information($"Searching Personal Shop {shopHandler}...");
            var PersonalShop = _mapServer.FindClientByTamerHandle(shopHandler);

            if (PersonalShop != null)
            {
                hasItem = false;
                var totalValue = boughtUnitPrice * boughtAmount;

                _logger.Information($"You spend {totalValue} bits");
                client.Tamer.Inventory.RemoveBits(totalValue);

                client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize());
                await _sender.Send(new UpdateItemListBitsCommand(client.Tamer.Inventory.Id, client.Tamer.Inventory.Bits));

                /*var Slot = PersonalShop.Tamer.TamerShop.Items.FirstOrDefault(x => x.ItemId == boughtItemId);
                PersonalShop.Tamer.TamerShop.Items[Slot.Slot].Amount -= boughtAmount;*/

                var totalValuewithDescount = (totalValue / 100) * 98;

                _logger.Debug($"Seller win {totalValuewithDescount} bits");
                PersonalShop.Tamer.Inventory.AddBits(totalValuewithDescount);

                PersonalShop.Send(new LoadInventoryPacket(PersonalShop.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize());
                await _sender.Send(new UpdateItemListBitsCommand(PersonalShop.Tamer.Inventory.Id, PersonalShop.Tamer.Inventory.Bits));

                _logger.Debug($"Tentando comprar Item em {PersonalShop.Tamer.ShopName} {shopHandler} » {shopHandler} {shopSlot} {boughtItemId} {boughtAmount} {boughtUnitPrice}.");
                var newItem = new ItemModel(boughtItemId, boughtAmount);
                newItem.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == boughtItemId));

                _logger.Debug($"Removing consigned shop bought item...");
                PersonalShop.Tamer.TamerShop.RemoveOrReduceItems(((ItemModel)newItem.Clone()).GetList());

                _logger.Debug($"Updating {PersonalShop.Tamer.Name} personal shop items...");
                await _sender.Send(new UpdateItemsCommand(PersonalShop.Tamer.TamerShop));

                PersonalShop.Tamer.TamerShop.CheckEmptyItems();

                _logger.Debug($"Adding bought item...");
                client.Tamer.Inventory.AddItems(((ItemModel)newItem.Clone()).GetList());

                _logger.Debug($"Updating item list...");
                client.Send(new ReceiveItemPacket(newItem, InventoryTypeEnum.Inventory));
                await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));

                _logger.Debug($"Sending consigned shop item list view packet...");
                client.Send(new PersonalShopItemsViewPacket(PersonalShop.Tamer.TamerShop, PersonalShop.Tamer.ShopName));
                PersonalShop.Send(new PersonalShopItemsViewPacket(PersonalShop.Tamer.TamerShop, PersonalShop.Tamer.ShopName));

                PersonalShop.Send(new NoticeMessagePacket($"You sold {newItem.Amount}x {newItem.ItemInfo.Name} for {client.Tamer.Name}!"));

                foreach (var item in PersonalShop.Tamer.TamerShop.Items.Where(x => x.ItemId > 0))
                {
                    hasItem = true;
                }

                if (hasItem == false)
                {
                    PersonalShop.Send(new NoticeMessagePacket($"Your personal shop as been closed!"));
                    PersonalShop.Tamer.UpdateCurrentCondition(ConditionEnum.Default);
                    PersonalShop.Send(new PersonalShopPacket());

                    _mapServer.BroadcastForTamerViewsAndSelf(PersonalShop.TamerId, new SyncConditionPacket(PersonalShop.Tamer.GeneralHandler, PersonalShop.Tamer.CurrentCondition).Serialize());
                }
            }
            else
            {
                _logger.Information($"PersonalShop not found ...");
            }
        }
    }
}
