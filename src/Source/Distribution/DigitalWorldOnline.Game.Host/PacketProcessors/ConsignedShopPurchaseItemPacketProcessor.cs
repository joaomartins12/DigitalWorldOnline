using AutoMapper;
using DigitalWorldOnline.Application;
using DigitalWorldOnline.Application.Separar.Commands.Delete;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Application.Separar.Queries;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Extensions;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Models.Character;
using DigitalWorldOnline.Commons.Models.TamerShop;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.Items;
using DigitalWorldOnline.Commons.Packets.PersonalShop;
using MediatR;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class ConsignedShopPurchaseItemPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.ConsignedShopPurchaseItem;

        private readonly AssetsLoader _assets;
        private readonly Serilog.ILogger _logger;
        private readonly IMapper _mapper;
        private readonly ISender _sender;

        public ConsignedShopPurchaseItemPacketProcessor(
            AssetsLoader assets,
            Serilog.ILogger logger,
            IMapper mapper,
            ISender sender)
        {
            _assets = assets;
            _logger = logger;
            _mapper = mapper;
            _sender = sender;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            try
            {
                var packet = new GamePacketReader(packetData);

                var shopHandler = packet.ReadInt();
                var shopSlot = packet.ReadInt();
                var boughtItemId = packet.ReadInt();
                var boughtAmount = packet.ReadInt();
                packet.Skip(60);
                var boughtUnitPrice = packet.ReadInt64();

                if (boughtAmount <= 0 || boughtUnitPrice <= 0 || boughtItemId <= 0)
                {
                    client.Send(new NoticeMessagePacket("Invalid purchase parameters."));
                    _logger.Warning("ConsignedShop: invalid params (slot={Slot}, item={Item}, amt={Amt}, price={Price}) from {Buyer}",
                        shopSlot, boughtItemId, boughtAmount, boughtUnitPrice, client.TamerId);
                    return;
                }

                // Loja
                var shopDto = await _sender.Send(new ConsignedShopByHandlerQuery(shopHandler));
                var shop = _mapper.Map<ConsignedShop>(shopDto);
                if (shop == null)
                {
                    client.Send(new UnloadConsignedShopPacket(shopHandler));
                    _logger.Debug("ConsignedShop: handler {Handler} not found.", shopHandler);
                    return;
                }

                // Dono da loja
                var seller = _mapper.Map<CharacterModel>(await _sender.Send(new CharacterAndItemsByIdQuery(shop.CharacterId)));
                if (seller == null)
                {
                    await _sender.Send(new DeleteConsignedShopCommand(shopHandler));
                    client.Send(new UnloadConsignedShopPacket(shopHandler));
                    _logger.Debug("ConsignedShop: owner {OwnerId} not found. Shop {Handler} deleted.", shop.CharacterId, shopHandler);
                    return;
                }

                if (seller.Name == client.Tamer.Name)
                {
                    client.Send(new NoticeMessagePacket("You cannot buy from your own store."));
                    return;
                }

                // Valida slot/listagem
                if (shopSlot < 0 || shopSlot >= seller.ConsignedShopItems.Items.Count)
                {
                    client.Send(new NoticeMessagePacket("Invalid shop slot."));
                    _logger.Warning("ConsignedShop: invalid slot {Slot} (seller={SellerId})", shopSlot, seller.Id);
                    return;
                }

                var listedItem = seller.ConsignedShopItems.Items[shopSlot];
                if (listedItem.ItemId != boughtItemId)
                {
                    client.Send(new NoticeMessagePacket("The item listing has changed."));
                    _logger.Warning("ConsignedShop: listing mismatch slot={Slot} expected={Exp} got={Got}",
                        shopSlot, listedItem.ItemId, boughtItemId);
                    return;
                }

                if (listedItem.Amount < boughtAmount)
                {
                    client.Send(new NoticeMessagePacket("Not enough stock in the listing."));
                    _logger.Warning("ConsignedShop: insufficient stock. slot={Slot} has={Has} want={Want}",
                        shopSlot, listedItem.Amount, boughtAmount);
                    return;
                }

                var assetInfo = _assets.ItemInfo.FirstOrDefault(x => x.ItemId == boughtItemId);
                if (assetInfo == null)
                {
                    client.Send(new NoticeMessagePacket("Item not found in assets."));
                    _logger.Warning("ConsignedShop: asset not found for ItemId={Item}", boughtItemId);
                    return;
                }

                var totalValue = boughtUnitPrice * boughtAmount;

                // Checa bits suficientes
                if (client.Tamer.Inventory.Bits < totalValue)
                {
                    client.Send(new NoticeMessagePacket("You do not have enough Bits."));
                    _logger.Debug("ConsignedShop: not enough bits. need={Need} have={Have} buyer={Buyer}",
                        totalValue, client.Tamer.Inventory.Bits, client.TamerId);
                    return;
                }

                // Prepara item a comprar e testa capacidade do inventário ANTES de debitar bits
                var newItem = new ItemModel(boughtItemId, boughtAmount);
                newItem.SetItemInfo(assetInfo);
                if (newItem.IsTemporary)
                {
                    var minutes = (uint)(newItem.ItemInfo?.UsageTimeMinutes ?? 0);
                    if (minutes > 0) newItem.SetRemainingTime(minutes);
                }

                var cloneToAdd = (ItemModel)newItem.Clone();
                var canAdd = client.Tamer.Inventory.AddItems(cloneToAdd.GetList());
                if (!canAdd)
                {
                    client.Send(new NoticeMessagePacket("Not enough space in inventory."));
                    _logger.Debug("ConsignedShop: inventory full for buyer={Buyer}", client.TamerId);
                    return;
                }

                // Agora que os itens entraram, debita os bits do comprador
                client.Tamer.Inventory.RemoveBits(totalValue);
                await _sender.Send(new UpdateItemListBitsCommand(client.Tamer.Inventory));

                // Persiste inventário do comprador (itens)
                await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));

                // Atualiza vendedor (online/offline)
                var sellerClient = client.Server.FindByTamerId(seller.Id);
                if (sellerClient != null && sellerClient.IsConnected)
                {
                    var itemName = assetInfo.Name ?? "item";
                    sellerClient.Send(new NoticeMessagePacket($"You sold {boughtAmount}x {itemName} to {client.Tamer.Name}!"));

                    sellerClient.Tamer.ConsignedWarehouse.AddBits(totalValue);
                    await _sender.Send(new UpdateItemListBitsCommand(sellerClient.Tamer.ConsignedWarehouse));

                    // remove da listagem do vendedor (inventário de shop)
                    seller.ConsignedShopItems.RemoveOrReduceItems(((ItemModel)newItem.Clone()).GetList());
                    await _sender.Send(new UpdateItemsCommand(seller.ConsignedShopItems));
                }
                else
                {
                    seller.ConsignedWarehouse.AddBits(totalValue);
                    seller.ConsignedShopItems.RemoveOrReduceItems(((ItemModel)newItem.Clone()).GetList());

                    await _sender.Send(new UpdateItemListBitsCommand(seller.ConsignedWarehouse));
                    await _sender.Send(new UpdateItemsCommand(seller.ConsignedShopItems));
                }

                // Se a loja ficou sem itens, fecha
                if (seller.ConsignedShopItems.Count == 0)
                {
                    await _sender.Send(new DeleteConsignedShopCommand(shopHandler));
                    client.Send(new UnloadConsignedShopPacket(shopHandler));
                    sellerClient?.Send(new ConsignedShopClosePacket());
                }
                else
                {
                    // Restaura ItemInfo das listagens remanescentes (para o packet de View)
                    seller.ConsignedShopItems.Items.ForEach(item =>
                    {
                        item.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == item.ItemId));
                    });
                }

                // Atualiza o comprador (UI)
                client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));
                client.Send(new ConsignedShopBoughtItemPacket(shopSlot, boughtAmount));
                client.Send(new ConsignedShopItemsViewPacket(_mapper.Map<ConsignedShop>(shopDto), seller.ConsignedShopItems, seller.Name));

                _logger.Information("ConsignedShop: buyer={Buyer} bought item={Item} x{Amt} for {Total} from seller={Seller} (shop={Shop})",
                    client.TamerId, boughtItemId, boughtAmount, totalValue, seller.Id, shopHandler);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "ConsignedShop: exception purchasing (buyer={Buyer})", client.TamerId);
                client.Send(new NoticeMessagePacket("An error occurred while purchasing the item."));
            }
        }
    }
}
