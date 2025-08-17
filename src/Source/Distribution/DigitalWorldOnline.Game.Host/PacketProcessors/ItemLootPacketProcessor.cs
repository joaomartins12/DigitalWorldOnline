using DigitalWorldOnline.Application;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Enums.Party;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Models.Character;

using DigitalWorldOnline.Commons.Models.Mechanics;

// using DigitalWorldOnline.Commons.Models.Map; // <- intencionalmente não usado
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Packets.Items;
using DigitalWorldOnline.Commons.Packets.MapServer;
using DigitalWorldOnline.Commons.Utils;
using DigitalWorldOnline.Game.Managers;
using DigitalWorldOnline.GameHost;
using MediatR;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class ItemLootPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.LootItem;

        private readonly PartyManager _partyManager;
        private readonly MapServer _mapServer;
        private readonly DungeonsServer _dungeonServer;
        private readonly AssetsLoader _assets;
        private readonly ISender _sender;
        private readonly ILogger _logger;

        private static readonly Random _rng = new Random();

        public ItemLootPacketProcessor(
            PartyManager partyManager,
            MapServer mapServer,
            AssetsLoader assets,
            ISender sender,
            ILogger logger,
            DungeonsServer dungeonServer)
        {
            _partyManager = partyManager;
            _assets = assets;
            _mapServer = mapServer;
            _sender = sender;
            _logger = logger;
            _dungeonServer = dungeonServer;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);
            var dropHandler = packet.ReadInt();

            // Tenta primeiro no mapa normal
            var targetDrop = _mapServer.GetDrop(client.Tamer.Location.MapId, dropHandler, client.TamerId);
            if (targetDrop == null)
            {
                // Tenta na dungeon
                var targetDungeonDrop = _dungeonServer.GetDrop(client.Tamer.Location.MapId, dropHandler, client.TamerId);
                if (targetDungeonDrop == null)
                {
                    client.Send(
                        UtilitiesFunctions.GroupPackets(
                            new SystemMessagePacket("Drop has no data.").Serialize(),
                            new PickItemFailPacket(PickItemFailReasonEnum.Unknow).Serialize(),
                            new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                        )
                    );

                    _dungeonServer.BroadcastForTamerViewsAndSelf(
                        client.TamerId,
                        new UnloadDropsPacket(dropHandler).Serialize());
                    return;
                }

                if (targetDungeonDrop.Collected)
                    return;

                await HandleLootInDungeon(client, targetDungeonDrop);
                return;
            }

            if (targetDrop.Collected)
                return;

            await HandleLootInMap(client, targetDrop);
        }

        // -------------------------- MAP --------------------------
        private async Task HandleLootInMap(GameClient client, DigitalWorldOnline.Commons.Models.Map.Drop targetDrop)
        {
            var dropClone = (ItemModel)targetDrop.DropInfo.Clone();
            var party = _partyManager.FindParty(client.TamerId);

            var freeForAll = party?.LootType == PartyLootShareTypeEnum.Free;
            var orderType = party?.LootType == PartyLootShareTypeEnum.Order;

            if (!(targetDrop.OwnerId == client.TamerId || freeForAll))
            {
                _logger.Verbose("Loot denied: tamer {TamerId} is not owner of this drop (item {ItemId}).", client.TamerId, targetDrop.DropInfo.ItemId);
                client.Send(new PickItemFailPacket(PickItemFailReasonEnum.NotTheOwner));
                return;
            }

            targetDrop.SetCollected(true);

            // Bits
            if (targetDrop.BitsDrop)
            {
                await DistributeBitsMap(client, party, dropClone.Amount);

                _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new UnloadDropsPacket(targetDrop).Serialize());
                _mapServer.RemoveDrop(targetDrop, client.TamerId);

                await UpdateItemListBits(client);
                client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));
                return;
            }

            // Item
            var itemInfo = _assets.ItemInfo.FirstOrDefault(x => x.ItemId == targetDrop.DropInfo.ItemId);
            if (itemInfo == null)
            {
                targetDrop.SetCollected(false);
                _logger.Warning("Item info missing for itemId {ItemId}.", targetDrop.DropInfo.ItemId);
                client.Send(
                    UtilitiesFunctions.GroupPackets(
                        new PickItemFailPacket(PickItemFailReasonEnum.Unknow).Serialize(),
                        new SystemMessagePacket($"Item has no data info {targetDrop.DropInfo.ItemId}.").Serialize(),
                        new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                    )
                );
                return;
            }

            var acquireClone = (ItemModel)targetDrop.DropInfo.Clone();
            targetDrop.DropInfo.SetItemInfo(itemInfo);
            dropClone.SetItemInfo(itemInfo);
            acquireClone.SetItemInfo(itemInfo);

            if (orderType != true)
            {
                if (client.Tamer.Inventory.AddItem(acquireClone))
                {
                    await UpdateItems(client);

                    _logger.Verbose("Tamer {TamerId} looted item {ItemId} x{Amount}.",
                        client.TamerId, dropClone.ItemId, dropClone.Amount);

                    _mapServer.BroadcastForTamerViewsAndSelf(
                        client.TamerId,
                        UtilitiesFunctions.GroupPackets(
                            new PickItemPacket(client.Tamer.AppearenceHandler, dropClone).Serialize(),
                            new UnloadDropsPacket(targetDrop).Serialize()
                        )
                    );

                    _mapServer.RemoveDrop(targetDrop, client.TamerId);

                    if (party != null)
                    {
                        foreach (var memberId in party.GetMembersIdList())
                        {
                            var targetPlayer = _mapServer.FindClientByTamerId(memberId)
                                               ?? _dungeonServer.FindClientByTamerId(memberId);
                            targetPlayer?.Send(new PartyLootItemPacket(client.Tamer, acquireClone).Serialize());
                        }
                    }
                }
                else
                {
                    targetDrop.SetCollected(false);
                    _logger.Verbose("Inventory full for tamer {TamerId} when looting item {ItemId}.", client.TamerId, targetDrop.DropInfo.ItemId);
                    client.Send(new PickItemFailPacket(PickItemFailReasonEnum.InventoryFull));
                }
            }
            else
            {
                // ORDER: sorteia um membro elegível (mesmo mapa e conectado); fallback pro looter
                var winner = PickEligiblePartyMemberOnMap(party, client) ?? client.Tamer;
                var diceNumber = (byte)_rng.Next(0, 256);

                var winnerClient = _mapServer.FindClientByTamerId(winner.Id);
                if (winnerClient != null && winnerClient.Tamer.Inventory.AddItem(acquireClone))
                {
                    await UpdateItems(winnerClient);

                    _logger.Verbose("Order loot: {Winner} got item {ItemId} x{Amount} (rolled {Dice}).",
                        winnerClient.Tamer.Name, dropClone.ItemId, dropClone.Amount, diceNumber);

                    _mapServer.BroadcastForTamerViewsAndSelf(
                        client.TamerId,
                        UtilitiesFunctions.GroupPackets(
                            new PickItemPacket(client.Tamer.AppearenceHandler, acquireClone).Serialize(),
                            new UnloadDropsPacket(targetDrop).Serialize()
                        )
                    );

                    _mapServer.RemoveDrop(targetDrop, client.TamerId);

                    if (party != null)
                    {
                        foreach (var memberId in party.GetMembersIdList())
                        {
                            var targetMsg = _mapServer.FindClientByTamerId(memberId);
                            targetMsg?.Send(new PartyLootItemPacket(winnerClient.Tamer, acquireClone, diceNumber, client.Tamer.Name).Serialize());
                        }
                    }
                }
                else
                {
                    targetDrop.SetCollected(false);
                    _logger.Verbose("Order loot failed: inventory full or winner disconnected (tamer {TamerId}).", client.TamerId);
                    (winnerClient ?? client).Send(new PickItemFailPacket(PickItemFailReasonEnum.InventoryFull));
                }
            }
        }

        // -------------------------- DUNGEON --------------------------
        private async Task HandleLootInDungeon(GameClient client, DigitalWorldOnline.Commons.Models.Map.Drop targetDrop)
        {
            var dropClone = (ItemModel)targetDrop.DropInfo.Clone();
            var party = _partyManager.FindParty(client.TamerId);

            var freeForAll = party?.LootType == PartyLootShareTypeEnum.Free;
            var orderType = party?.LootType == PartyLootShareTypeEnum.Order;

            if (!(targetDrop.OwnerId == client.TamerId || freeForAll))
            {
                _logger.Verbose("Loot denied (dungeon): tamer {TamerId} is not owner of this drop (item {ItemId}).", client.TamerId, targetDrop.DropInfo.ItemId);
                client.Send(new PickItemFailPacket(PickItemFailReasonEnum.NotTheOwner));
                return;
            }

            targetDrop.SetCollected(true);

            // Bits
            if (targetDrop.BitsDrop)
            {
                await DistributeBitsDungeon(client, party, dropClone.Amount);

                _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, new UnloadDropsPacket(targetDrop).Serialize());
                _dungeonServer.RemoveDrop(targetDrop, client.TamerId);

                await UpdateItemListBits(client);
                client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));
                return;
            }

            // Item
            var itemInfo = _assets.ItemInfo.FirstOrDefault(x => x.ItemId == targetDrop.DropInfo.ItemId);
            if (itemInfo == null)
            {
                targetDrop.SetCollected(false);
                _logger.Warning("Item info missing for itemId {ItemId} (dungeon).", targetDrop.DropInfo.ItemId);
                client.Send(
                    UtilitiesFunctions.GroupPackets(
                        new PickItemFailPacket(PickItemFailReasonEnum.Unknow).Serialize(),
                        new SystemMessagePacket($"Item has no data info {targetDrop.DropInfo.ItemId}.").Serialize(),
                        new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                    )
                );
                return;
            }

            var acquireClone = (ItemModel)targetDrop.DropInfo.Clone();
            targetDrop.DropInfo.SetItemInfo(itemInfo);
            dropClone.SetItemInfo(itemInfo);
            acquireClone.SetItemInfo(itemInfo);

            if (orderType != true)
            {
                if (client.Tamer.Inventory.AddItem(acquireClone))
                {
                    await UpdateItems(client);

                    _logger.Verbose("Tamer {TamerId} looted (dungeon) item {ItemId} x{Amount}.",
                        client.TamerId, dropClone.ItemId, dropClone.Amount);

                    _dungeonServer.BroadcastForTamerViewsAndSelf(
                        client.TamerId,
                        UtilitiesFunctions.GroupPackets(
                            new PickItemPacket(client.Tamer.AppearenceHandler, dropClone).Serialize(),
                            new UnloadDropsPacket(targetDrop).Serialize()
                        )
                    );

                    _dungeonServer.RemoveDrop(targetDrop, client.TamerId);

                    if (party != null)
                    {
                        foreach (var member in party.Members.Values)
                        {
                            var targetClient = _dungeonServer.FindClientByTamerId(member.Id);
                            if (targetClient != null && member.Id != client.Tamer.Id)
                                targetClient.Send(new PartyLootItemPacket(client.Tamer, acquireClone).Serialize());
                        }
                    }
                }
                else
                {
                    targetDrop.SetCollected(false);
                    _logger.Verbose("Inventory full (dungeon) for tamer {TamerId} on item {ItemId}.", client.TamerId, targetDrop.DropInfo.ItemId);
                    client.Send(new PickItemFailPacket(PickItemFailReasonEnum.InventoryFull));
                }
            }
            else
            {
                // ORDER em dungeon
                var winner = PickEligiblePartyMemberInDungeon(party, client) ?? client.Tamer;
                var diceNumber = (byte)_rng.Next(0, 256);

                var winnerClient = _dungeonServer.FindClientByTamerId(winner.Id);
                if (winnerClient != null && winnerClient.Tamer.Inventory.AddItem(acquireClone))
                {
                    await UpdateItems(winnerClient);

                    _logger.Verbose("Order loot (dungeon): {Winner} got item {ItemId} x{Amount} (rolled {Dice}).",
                        winnerClient.Tamer.Name, dropClone.ItemId, dropClone.Amount, diceNumber);

                    _dungeonServer.BroadcastForTamerViewsAndSelf(
                        client.TamerId,
                        UtilitiesFunctions.GroupPackets(
                            new PickItemPacket(client.Tamer.AppearenceHandler, acquireClone).Serialize(),
                            new UnloadDropsPacket(targetDrop).Serialize()
                        )
                    );

                    _dungeonServer.RemoveDrop(targetDrop, client.TamerId);

                    if (party != null)
                    {
                        foreach (var member in party.Members.Values)
                        {
                            var targetClient = _dungeonServer.FindClientByTamerId(member.Id);
                            if (targetClient != null)
                                targetClient.Send(new PartyLootItemPacket(winnerClient.Tamer, acquireClone, diceNumber, client.Tamer.Name).Serialize());
                        }
                    }
                }
                else
                {
                    targetDrop.SetCollected(false);
                    _logger.Verbose("Order loot failed (dungeon): inventory full or winner disconnected (tamer {TamerId}).", client.TamerId);
                    (winnerClient ?? client).Send(new PickItemFailPacket(PickItemFailReasonEnum.InventoryFull));
                }
            }
        }

        // -------------------------- Bits split helpers --------------------------
        private async Task DistributeBitsMap(GameClient looter, GameParty? party, int amount)
        {
            if (party == null)
            {
                looter.Send(new PickBitsPacket(looter.Tamer.GeneralHandler, amount));
                looter.Tamer.Inventory.AddBits(amount);
                _logger.Verbose("Tamer {TamerId} looted bits x{Amount}.", looter.TamerId, amount);
                return;
            }

            var eligible = new List<GameClient>();
            foreach (var id in party.Members.Values.Select(m => m.Id))
            {
                var c = _mapServer.FindClientByTamerId(id);
                if (c != null && c.IsConnected && c.Tamer.Location.MapId == looter.Tamer.Location.MapId)
                    eligible.Add(c);
            }
            if (!eligible.Contains(looter)) eligible.Add(looter);

            var share = amount / eligible.Count;
            var remainder = amount % eligible.Count;

            foreach (var c in eligible)
            {
                var add = share + (c == looter ? remainder : 0);
                c.Tamer.Inventory.AddBits(add);
                await UpdateItemListBits(c);
                c.Send(new PickBitsPacket(c.Tamer.GeneralHandler, add));
            }

            _logger.Verbose("Tamer {TamerId} looted bits x{Share} for party {PartyId}.", looter.TamerId, share, party.Id);
        }

        private async Task DistributeBitsDungeon(GameClient looter, GameParty? party, int amount)
        {
            if (party == null)
            {
                looter.Send(new PickBitsPacket(looter.Tamer.GeneralHandler, amount));
                looter.Tamer.Inventory.AddBits(amount);
                _logger.Verbose("Tamer {TamerId} looted (dungeon) bits x{Amount}.", looter.TamerId, amount);
                return;
            }

            var eligible = new List<GameClient>();
            foreach (var id in party.Members.Values.Select(m => m.Id))
            {
                var c = _dungeonServer.FindClientByTamerId(id);
                if (c != null && c.IsConnected && c.Tamer.Location.MapId == looter.Tamer.Location.MapId)
                    eligible.Add(c);
            }
            if (!eligible.Contains(looter)) eligible.Add(looter);

            var share = amount / eligible.Count;
            var remainder = amount % eligible.Count;

            foreach (var c in eligible)
            {
                var add = share + (c == looter ? remainder : 0);
                c.Tamer.Inventory.AddBits(add);
                await UpdateItemListBits(c);
                c.Send(new PickBitsPacket(c.Tamer.GeneralHandler, add));
            }

            _logger.Verbose("Tamer {TamerId} looted (dungeon) bits x{Share} for party {PartyId}.", looter.TamerId, share, party.Id);
        }

        // -------------------------- Party winner helpers --------------------------
        private CharacterModel? PickEligiblePartyMemberOnMap(GameParty? party, GameClient looter)
        {
            if (party == null || party.Members.Count == 0) return null;

            var eligible = party.Members.Values
                .Select(m => _mapServer.FindClientByTamerId(m.Id))
                .Where(c => c != null && c.IsConnected && c.Tamer.Location.MapId == looter.Tamer.Location.MapId)
                .Select(c => c!.Tamer)
                .ToList();

            if (eligible.Count == 0) return null;
            return eligible[_rng.Next(eligible.Count)];
        }

        private CharacterModel? PickEligiblePartyMemberInDungeon(GameParty? party, GameClient looter)
        {
            if (party == null || party.Members.Count == 0) return null;

            var eligible = party.Members.Values
                .Select(m => _dungeonServer.FindClientByTamerId(m.Id))
                .Where(c => c != null && c.IsConnected && c.Tamer.Location.MapId == looter.Tamer.Location.MapId)
                .Select(c => c!.Tamer)
                .ToList();

            if (eligible.Count == 0) return null;
            return eligible[_rng.Next(eligible.Count)];
        }

        // -------------------------- Persistence helpers --------------------------
        private Task UpdateItems(GameClient client)
            => _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));

        private Task UpdateItemListBits(GameClient client)
            => _sender.Send(new UpdateItemListBitsCommand(client.Tamer.Inventory));
    }
}
