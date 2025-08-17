using DigitalWorldOnline.Application;
using DigitalWorldOnline.Application.Separar.Commands.Create;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Application.Separar.Queries;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums;
using DigitalWorldOnline.Commons.Enums.Character;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Extensions;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Models.Character;
using DigitalWorldOnline.Commons.Models.Config;
using DigitalWorldOnline.Commons.Models.Digimon;
using DigitalWorldOnline.Commons.Models.Summon;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Packets.Items;
using DigitalWorldOnline.Commons.Packets.MapServer;
using DigitalWorldOnline.Commons.Utils;
using DigitalWorldOnline.Game.Managers;
using DigitalWorldOnline.GameHost;
using MediatR;
using Microsoft.Extensions.Configuration;
using Serilog;
using System;
using System.Linq;
using System.Collections.Generic;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class ItemConsumePacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.ConsumeItem;

        private const string GamerServerPublic = "GameServer:PublicAddress";
        private const string GameServerPort = "GameServer:Port";

        private readonly StatusManager _statusManager;
        private readonly MapServer _mapServer;
        private readonly DungeonsServer _dungeonServer;
        private readonly AssetsLoader _assets;
        private readonly ConfigsLoader _configs;
        private readonly ExpManager _expManager;
        private readonly ISender _sender;
        private readonly ILogger _logger;
        private readonly IConfiguration _configuration;

        public ItemConsumePacketProcessor(
            StatusManager statusManager,
            MapServer mapServer,
            DungeonsServer dungeonsServer,
            AssetsLoader assets,
            ExpManager expManager,
            ConfigsLoader configs,
            ISender sender,
            ILogger logger,
            IConfiguration configuration)
        {
            _statusManager = statusManager;
            _mapServer = mapServer;
            _dungeonServer = dungeonsServer;
            _expManager = expManager;
            _assets = assets;
            _configs = configs;
            _sender = sender;
            _logger = logger;
            _configuration = configuration;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            try
            {
                var packet = new GamePacketReader(packetData);

                packet.Skip(4);
                var itemSlot = packet.ReadShort();

                if (client.Partner == null)
                {
                    _logger.Warning("ItemConsume: invalid partner for tamer {TamerId}.", client.TamerId);
                    client.Send(new SystemMessagePacket("Invalid partner."));
                    return;
                }

                if (itemSlot < 0)
                {
                    client.Send(new SystemMessagePacket("Invalid slot."));
                    return;
                }

                var targetItem = client.Tamer.Inventory.FindItemBySlot(itemSlot);
                if (targetItem == null || targetItem.ItemInfo == null)
                {
                    _logger.Warning("ItemConsume: invalid item at slot {Slot} (tamer {TamerId}).", itemSlot, client.TamerId);
                    client.Send(new SystemMessagePacket($"Invalid item at slot {itemSlot}."));
                    return;
                }

                // EXP items (tipos 60/78/85/86) — mantida tua lógica com hardening
                if (targetItem.ItemInfo.Type == 60 || targetItem.ItemInfo.Type == 78 || targetItem.ItemInfo.Type == 85 || targetItem.ItemInfo.Type == 86)
                {
                    if (targetItem.ItemInfo?.SkillInfo == null)
                    {
                        client.Send(
                            UtilitiesFunctions.GroupPackets(
                                new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type).Serialize(),
                                new SystemMessagePacket($"Invalid skill info for item id {targetItem.ItemId}.").Serialize(),
                                new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                            )
                        );
                        _logger.Warning("ItemConsume: invalid skill info for item {ItemId} (tamer {TamerId}).", targetItem.ItemId, client.TamerId);
                        return;
                    }

                    foreach (var apply in targetItem.ItemInfo.SkillInfo.Apply)
                    {
                        if (apply.Type != SkillCodeApplyTypeEnum.Default || apply.Attribute != SkillCodeApplyAttributeEnum.EXP)
                            continue;

                        long baseValue = Convert.ToInt64(apply.Value);

                        switch (targetItem.ItemInfo.Target)
                        {
                            case ItemConsumeTargetEnum.Both:
                                {
                                    // Tamer
                                    long tamerValue = baseValue;
                                    if (client.Tamer.Level >= (int)GeneralSizeEnum.TamerLevelMax) tamerValue = 0;
                                    var tamerRes = _expManager.ReceiveTamerExperience(tamerValue, client.Tamer);

                                    if (!tamerRes.Success)
                                    {
                                        client.Send(new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type, ItemConsumeFailEnum.UseLimitReached));
                                        return;
                                    }

                                    client.Send(new ReceiveExpPacket(
                                        tamerValue,
                                        0,
                                        client.Tamer.CurrentExperience,
                                        client.Tamer.Partner.GeneralHandler,
                                        0,
                                        0,
                                        client.Tamer.Partner.CurrentExperience,
                                        0
                                    ));

                                    if (tamerRes.LevelGain > 0)
                                    {
                                        client.Tamer.SetLevelStatus(_statusManager.GetTamerLevelStatus(client.Tamer.Model, client.Tamer.Level));
                                        BroadcastLevelUp(client, client.Tamer.GeneralHandler, client.Tamer.Level);
                                        client.Tamer.FullHeal();
                                        client.Send(new UpdateStatusPacket(client.Tamer));
                                    }

                                    if (tamerRes.Success)
                                        await _sender.Send(new UpdateCharacterExperienceCommand(client.TamerId, client.Tamer.CurrentExperience, client.Tamer.Level));

                                    // Digimon
                                    long digiValue = baseValue;
                                    if (client.Tamer.Partner.Level >= (int)GeneralSizeEnum.DigimonLevelMax) digiValue = 0;
                                    var digiRes = _expManager.ReceiveDigimonExperience(digiValue, client.Tamer.Partner);

                                    if (digiRes.Success)
                                    {
                                        client.Send(new ReceiveExpPacket(
                                            0,
                                            0,
                                            client.Tamer.CurrentExperience,
                                            client.Tamer.Partner.GeneralHandler,
                                            digiValue,
                                            0,
                                            client.Tamer.Partner.CurrentExperience,
                                            0
                                        ));
                                    }

                                    if (digiRes.LevelGain > 0)
                                    {
                                        client.Partner.SetBaseStatus(_statusManager.GetDigimonBaseStatus(client.Partner.CurrentType, client.Partner.Level, client.Partner.Size));
                                        BroadcastLevelUp(client, client.Partner.GeneralHandler, client.Partner.Level);
                                        client.Partner.FullHeal();
                                        client.Send(new UpdateStatusPacket(client.Tamer));
                                    }

                                    if (digiRes.Success)
                                        await _sender.Send(new UpdateDigimonExperienceCommand(client.Partner));

                                    break;
                                }

                            case ItemConsumeTargetEnum.Digimon:
                                {
                                    long value = baseValue;
                                    if (client.Tamer.Partner.Level >= (int)GeneralSizeEnum.DigimonLevelMax) value = 0;

                                    var res = _expManager.ReceiveDigimonExperience(value, client.Tamer.Partner);
                                    if (!res.Success)
                                    {
                                        client.Send(new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type, ItemConsumeFailEnum.UseLimitReached));
                                        return;
                                    }

                                    client.Send(new ReceiveExpPacket(
                                        0, 0, client.Tamer.CurrentExperience,
                                        client.Tamer.Partner.GeneralHandler, value, 0, client.Tamer.Partner.CurrentExperience, 0));

                                    if (res.LevelGain > 0)
                                    {
                                        client.Partner.SetBaseStatus(_statusManager.GetDigimonBaseStatus(client.Partner.CurrentType, client.Partner.Level, client.Partner.Size));
                                        BroadcastLevelUp(client, client.Partner.GeneralHandler, client.Partner.Level);
                                        client.Partner.FullHeal();
                                        client.Send(new UpdateStatusPacket(client.Tamer));
                                    }

                                    if (res.Success)
                                        await _sender.Send(new UpdateDigimonExperienceCommand(client.Partner));
                                    break;
                                }

                            case ItemConsumeTargetEnum.Tamer:
                                {
                                    long value = baseValue;
                                    if (client.Tamer.Level >= (int)GeneralSizeEnum.TamerLevelMax) value = 0;

                                    var res = _expManager.ReceiveTamerExperience(value, client.Tamer);
                                    if (!res.Success)
                                    {
                                        client.Send(new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type, ItemConsumeFailEnum.InCooldown));
                                        return;
                                    }

                                    client.Send(new ReceiveExpPacket(
                                        value, 0, client.Tamer.CurrentExperience,
                                        client.Tamer.Partner.GeneralHandler, 0, 0, client.Tamer.Partner.CurrentExperience, 0));

                                    if (res.LevelGain > 0)
                                    {
                                        client.Tamer.SetLevelStatus(_statusManager.GetTamerLevelStatus(client.Tamer.Model, client.Tamer.Level));
                                        BroadcastLevelUp(client, client.Tamer.GeneralHandler, client.Tamer.Level);
                                        client.Tamer.FullHeal();
                                        client.Send(new UpdateStatusPacket(client.Tamer));
                                    }

                                    if (res.Success)
                                        await _sender.Send(new UpdateCharacterExperienceCommand(client.TamerId, client.Tamer.CurrentExperience, client.Tamer.Level));
                                    break;
                                }
                        }
                    }

                    BroadcastHpRates(client);

                    _logger.Verbose("Character {TamerId} consumed {ItemId}.", client.TamerId, targetItem.ItemId);

                    client.Tamer.Inventory.RemoveOrReduceItem(targetItem, 1, itemSlot);
                    await _sender.Send(new UpdateItemCommand(targetItem));

                    client.Send(UtilitiesFunctions.GroupPackets(
                        new ItemConsumeSuccessPacket(client.Tamer.GeneralHandler, itemSlot).Serialize(),
                        new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                    ));
                    return;
                }

                // Demais tipos (mantidos com hardening e fixes)
                if (targetItem.ItemInfo.Type == 61)
                    await ConsumeFoodItem(client, itemSlot, targetItem);
                else if (targetItem.ItemInfo.Type == 62)
                {
                    var summonInfo = _assets.SummonInfo.FirstOrDefault(x => x.ItemId == targetItem.ItemId);
                    if (summonInfo != null)
                        await SummonMonster(client, itemSlot, targetItem, summonInfo);
                    else
                        await ConsumeAchievement(client, itemSlot, targetItem);
                }
                else if (targetItem.ItemInfo.Type == 63)
                    await BuffItem(client, itemSlot, targetItem);
                else if (targetItem.ItemInfo.Type == 89)
                    await Fruits(client, itemSlot, targetItem);
                else if (targetItem.ItemInfo.Type == 90)
                    await Transcend(client, itemSlot, targetItem);
                else if (targetItem.ItemInfo.Type == 155)
                    await IncreaseInventorySlots(client, itemSlot, targetItem);
                else if (targetItem.ItemInfo.Type == 156)
                    await IncreaseWarehouseSlots(client, itemSlot, targetItem);
                else if (targetItem.ItemInfo.Type == 159)
                    await IncreaseDigimonSlots(client, itemSlot, targetItem);
                else if (targetItem.ItemInfo.Type == 160)
                    await IncreaseArchiveSlots(client, itemSlot, targetItem);
                else if (targetItem.ItemInfo.Type == 170 && targetItem.ItemInfo.Section == 17000)
                    await ContainerItem(client, itemSlot, targetItem);
                else if (targetItem.ItemInfo.Type == 180)
                    await CashTamerSkills(client, itemSlot, targetItem);
                else if (targetItem.ItemInfo.Type == 201)
                    await ConsumeFoodItem(client, itemSlot, targetItem);
                else if (targetItem.ItemInfo.Type == 71)
                    await ConsumeExpItem(client, itemSlot, targetItem);
                else if (targetItem.ItemInfo.Type == 72)
                    await BombTeleport(client, itemSlot, targetItem); // <— renomeado param
                else if (targetItem.ItemInfo.Type == 170 && targetItem.ItemInfo.Section == 9400)
                    await HatchItem(client, itemSlot, targetItem);
                else
                    client.Send(new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "ItemConsume: exception for TamerId={TamerId}", client?.TamerId);
                // Falha genérica — evita o jogador ficar travado
            }
        }

        // ---------- Helpers ----------
        private void BroadcastHpRates(GameClient client)
        {
            var payload = UtilitiesFunctions.GroupPackets(
                new UpdateCurrentHPRatePacket(client.Tamer.GeneralHandler, client.Tamer.HpRate).Serialize(),
                new UpdateCurrentHPRatePacket(client.Tamer.Partner.GeneralHandler, client.Tamer.Partner.HpRate).Serialize()
            );

            if (client.DungeonMap)
                _dungeonServer.BroadcastForTargetTamers(client.TamerId, payload);
            else
                _mapServer.BroadcastForTargetTamers(client.TamerId, payload);
        }

        private void BroadcastLevelUp(GameClient client, int generalHandler, int level)
        {
            var pkt = new LevelUpPacket(generalHandler, (byte)level).Serialize(); // <- cast para byte
            if (client.DungeonMap)
                _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, pkt);
            else
                _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, pkt);
        }

        // --------- abaixo: os teus métodos originais, com pequenos hardenings/fixes onde necessário ---------

        private async Task HatchItem(GameClient client, short itemSlot, ItemModel targetItem)
        {
            var free = false;

            var hatchInfo = _assets.Hatchs.FirstOrDefault(x => x.ItemId == targetItem.ItemInfo.ItemId);
            if (hatchInfo == null)
            {
                _logger.Warning("Unknown hatch info for egg {EggId}.", targetItem.ItemInfo.ItemId);
                client.Send(new SystemMessagePacket($"Unknown hatch info for egg {targetItem.ItemInfo.ItemId}."));
                return;
            }

            byte i = 0;
            while (i < client.Tamer.DigimonSlots)
            {
                if (client.Tamer.Digimons.FirstOrDefault(x => x.Slot == i) == null)
                {
                    free = true;
                    break;
                }
                i++;
            }

            if (free)
            {
                DigimonModel newDigimon;
                newDigimon = DigimonModel.Create("digiName", hatchInfo.HatchType, hatchInfo.HatchType, DigimonHatchGradeEnum.Perfect, 12500, i);

                newDigimon.NewLocation(client.Tamer.Location.MapId, client.Tamer.Location.X, client.Tamer.Location.Y);
                newDigimon.SetBaseInfo(_statusManager.GetDigimonBaseInfo(newDigimon.BaseType));
                newDigimon.SetBaseStatus(_statusManager.GetDigimonBaseStatus(newDigimon.BaseType, newDigimon.Level, newDigimon.Size));
                newDigimon.AddEvolutions(_assets.EvolutionInfo.First(x => x.Type == newDigimon.BaseType));

                if (newDigimon.BaseInfo == null || newDigimon.BaseStatus == null || !newDigimon.Evolutions.Any())
                {
                    _logger.Warning("Unknown digimon info for {Type}.", newDigimon.BaseType);
                    client.Send(new SystemMessagePacket($"Unknown digimon info for {newDigimon.BaseType}."));
                    return;
                }

                newDigimon.SetTamer(client.Tamer);
                client.Tamer.AddDigimon(newDigimon);

                client.Send(new HatchFinishPacket(newDigimon, (ushort)(client.Partner.GeneralHandler + 1000), client.Tamer.Digimons.FindIndex(x => x == newDigimon)));

                var digimonInfo = await _sender.Send(new CreateDigimonCommand(newDigimon));

                if (digimonInfo != null)
                {
                    newDigimon.SetId(digimonInfo.Id);
                    var slot = -1;

                    foreach (var digimon in newDigimon.Evolutions)
                    {
                        slot++;
                        var evolution = digimonInfo.Evolutions[slot];
                        if (evolution != null)
                        {
                            digimon.SetId(evolution.Id);
                            var skillSlot = -1;
                            foreach (var skill in digimon.Skills)
                            {
                                skillSlot++;
                                var dtoSkill = evolution.Skills[skillSlot];
                                skill.SetId(dtoSkill.Id);
                            }
                        }
                    }
                }

                client.Tamer.Inventory.RemoveOrReduceItem(targetItem, 1, itemSlot);
                await _sender.Send(new UpdateItemCommand(targetItem));

                client.Send(UtilitiesFunctions.GroupPackets(
                    new ItemConsumeSuccessPacket(client.Tamer.GeneralHandler, itemSlot).Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()));
            }
            else
            {
                client.Send(new SystemMessagePacket("You don't have free space to hatch digimon"));
                client.Send(new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type));
            }
        }

        private async Task CashTamerSkills(GameClient client, short itemSlot, ItemModel targetItem)
        {
            if (client.Tamer.TamerSkill.EquippedItems.Count == 5)
            {
                client.Send(new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type));
                return;
            }

            var targetSkill = _assets.TamerSkills.FirstOrDefault(x => x.SkillId == targetItem.ItemInfo?.SkillCode);
            if (targetSkill != null)
                targetItem.ItemInfo?.SetSkillInfo(_assets.SkillCodeInfo.FirstOrDefault(x => x.SkillCode == targetSkill.SkillCode));

            if (targetItem.ItemInfo?.SkillInfo == null)
            {
                client.Send(
                    UtilitiesFunctions.GroupPackets(
                        new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type).Serialize(),
                        new SystemMessagePacket($"Invalid skill info for item id {targetItem.ItemId}.").Serialize(),
                        new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                    )
                );
                _logger.Warning("CashTamerSkills: invalid skill info for item {ItemId} (tamer {TamerId}).", targetItem.ItemId, client.TamerId);
                return;
            }

            var activeSkill = client.Tamer.ActiveSkill.FirstOrDefault(x => x.SkillId == 0 || x.SkillId == targetSkill?.SkillId);
            if (activeSkill != null)
            {
                if (activeSkill.SkillId == targetSkill?.SkillId)
                    activeSkill.IncreaseEndDate(targetItem.ItemInfo.UsageTimeMinutes);
                else
                    activeSkill.SetTamerSkill(targetSkill!.SkillId, 0, TamerSkillTypeEnum.Cash, targetItem.ItemInfo.UsageTimeMinutes);
            }

            await _sender.Send(new UpdateTamerSkillCooldownByIdCommand(activeSkill));

            client.Tamer.Inventory.RemoveOrReduceItem(targetItem, 1);
            await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));

            client.Send(UtilitiesFunctions.GroupPackets(
                new ItemConsumeSuccessPacket(client.Tamer.GeneralHandler, itemSlot).Serialize(),
                new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize(),
                new ActiveTamerCashSkill(activeSkill!.SkillId, UtilitiesFunctions.RemainingTimeMinutes(activeSkill.RemainingMinutes)).Serialize()
            ));
        }

        private async Task SummonMonster(GameClient client, short itemSlot, ItemModel targetItem, SummonModel? SummonInfo)
        {
            if (!SummonInfo!.Maps.Contains(client.Tamer.Location.MapId))
            {
                client.Send(new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type, ItemConsumeFailEnum.InvalidArea));
                return;
            }

            var count = 0;
            foreach (var mobToAdd in SummonInfo.SummonedMobs)
            {
                count++;

                var mob = (SummonMobModel)mobToAdd.Clone();
                mob.TamersViewing.Clear();

                if (mob?.Location?.X != 0 && mob?.Location?.Y != 0)
                {
                    var diff = UtilitiesFunctions.CalculateDistance(mob.Location.X, client.Tamer.Location.X, mob.Location.Y, client.Tamer.Location.Y);
                    if (diff > 5000)
                    {
                        client.Send(new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type, ItemConsumeFailEnum.InvalidArea));
                        break;
                    }
                    else if (count == 1)
                    {
                        client.Tamer.Inventory.RemoveOrReduceItem(targetItem, 1);
                        await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
                        client.Send(UtilitiesFunctions.GroupPackets(
                            new ItemConsumeSuccessPacket(client.Tamer.GeneralHandler, itemSlot).Serialize(),
                            new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                        ));
                    }
                }
                else
                {
                    client.Tamer.Inventory.RemoveOrReduceItem(targetItem, 1);
                    await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
                    client.Send(UtilitiesFunctions.GroupPackets(
                        new ItemConsumeSuccessPacket(client.Tamer.GeneralHandler, itemSlot).Serialize(),
                        new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                    ));
                }

                int radius = 1000;
                var random = new Random();
                int xOffset = random.Next(-radius, radius + 1);
                int yOffset = random.Next(-radius, radius + 1);

                int bossX = client.Tamer.Location.X + xOffset;
                int bossY = client.Tamer.Location.Y + yOffset;

                if (client.DungeonMap)
                {
                    var map = _dungeonServer.Maps.FirstOrDefault(x => x.Clients.Exists(x => x.TamerId == client.TamerId));
                    var mobId = map.SummonMobs.Count + 1;

                    mob.SetId(mobId);

                    if (mob?.Location?.X != 0 && mob?.Location?.Y != 0)
                    {
                        bossX = mob.Location.X + xOffset;
                        bossY = mob.Location.Y + yOffset;
                        mob.SetLocation(client.Tamer.Location.MapId, bossX, bossY);
                    }
                    else
                    {
                        mob.SetLocation(client.Tamer.Location.MapId, bossX, bossY);
                    }

                    mob.SetDuration();
                    mob.SetTargetSummonHandle(client.Tamer.GeneralHandler);
                    _dungeonServer.AddSummonMobs(client.Tamer.Location.MapId, mob, client.TamerId);
                }
                else
                {
                    var map = _mapServer.Maps.FirstOrDefault(x => x.MapId == client.Tamer.Location.MapId);
                    var mobId = map.SummonMobs.Count + 1;

                    mob.SetId(mobId);

                    if (mob?.Location?.X != 0 && mob?.Location?.Y != 0)
                    {
                        bossX = mob.Location.X;
                        bossY = mob.Location.Y;
                        mob.SetLocation(client.Tamer.Location.MapId, bossX, bossY);
                    }
                    else
                    {
                        mob.SetLocation(client.Tamer.Location.MapId, bossX, bossY);
                    }

                    mob.SetDuration();
                    mob.SetTargetSummonHandle(client.Tamer.GeneralHandler);
                    _mapServer.AddSummonMobs(client.Tamer.Location.MapId, mob, client.TamerId);
                }
            }
        }

        private async Task ConsumeAchievement(GameClient client, short itemSlot, ItemModel targetItem)
        {
            client.Tamer.Inventory.RemoveOrReduceItem(targetItem, 1);
            client.Send(UtilitiesFunctions.GroupPackets(
                new ItemConsumeSuccessPacket(client.Tamer.GeneralHandler, itemSlot).Serialize(),
                new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
            ));
            await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
        }

        private async Task BombTeleport(GameClient client, short itemSlot, ItemModel targetItem)
        {
            var itemMapIdMapping = new Dictionary<int, int>
            {
                { 25001, 3 },
                { 9025, 3 },
                { 25003, 1100 },
                { 9027, 1100 },
                { 25006, 2100 },
                { 25019, 2100 },
                { 25004, 1103 },
                { 9028, 1103 }
            };

            if (!itemMapIdMapping.TryGetValue(targetItem.ItemId, out var mapId))
            {
                _logger.Warning("BombTeleport: ItemID {ItemId} not mapped.", targetItem.ItemId);
                return;
            }

            var waypoints = await _sender.Send(new MapRegionListAssetsByMapIdQuery(mapId));
            var destination = waypoints?.Regions?.FirstOrDefault();
            if (destination == null)
            {
                client.Send(new SystemMessagePacket($"Map information not found for map Id {mapId}."));
                _logger.Warning("BombTeleport: map info not found for map {MapId}", mapId);
                return;
            }

            switch (mapId)
            {
                case 3: destination.X = 19981; destination.Y = 14501; break;
                case 1100: destination.X = 21377; destination.Y = 56675; break;
                case 2100: destination.X = 9425; destination.Y = 9680; break;
                case 1103: destination.X = 4847; destination.Y = 39008; break;
            }

            client.Tamer.NewLocation(mapId, destination.X, destination.Y);
            await _sender.Send(new UpdateCharacterLocationCommand(client.Tamer.Location));

            client.Tamer.Partner.NewLocation(mapId, destination.X, destination.Y);
            await _sender.Send(new UpdateDigimonLocationCommand(client.Tamer.Partner.Location));

            client.Tamer.UpdateState(CharacterStateEnum.Loading);
            await _sender.Send(new UpdateCharacterStateCommand(client.TamerId, CharacterStateEnum.Loading));

            client.SetGameQuit(false);

            client.Send(new MapSwapPacket(
                _configuration[GamerServerPublic],
                _configuration[GameServerPort],
                client.Tamer.Location.MapId,
                client.Tamer.Location.X,
                client.Tamer.Location.Y
            ).Serialize());

            client.Tamer.Inventory.RemoveOrReduceItem(targetItem, 1, itemSlot);
            await _sender.Send(new UpdateItemCommand(targetItem));
        }

        private async Task Fruits(GameClient client, short itemSlot, ItemModel targetItem)
        {
            var fruitConfig = _configs.Fruits.FirstOrDefault(x => x.ItemId == targetItem.ItemId);
            if (fruitConfig == null)
            {
                client.Send(UtilitiesFunctions.GroupPackets(
                    new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type).Serialize(),
                    new SystemMessagePacket($"Invalid fruit config for item {targetItem.ItemId}.").Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                ));
                _logger.Error("Invalid fruit config for item {ItemId}.", targetItem.ItemId);
                return;
            }

            var sizeList = fruitConfig.SizeList.Where(x => x.HatchGrade == client.Partner.HatchGrade && x.Size > 1);
            if (!sizeList.Any())
            {
                client.Send(UtilitiesFunctions.GroupPackets(
                    new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type).Serialize(),
                    new SystemMessagePacket($"Invalid size list for fruit {targetItem.ItemId} and {client.Partner.HatchGrade} grade.").Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                ));
                _logger.Error("Invalid size list for fruit {ItemId} and grade {Grade}.", targetItem.ItemId, client.Partner.HatchGrade);
                return;
            }

            short newSize = 0;
            var changeSize = false;
            bool rare = false;
            while (!changeSize)
            {
                var availableSizes = sizeList.Randomize();
                foreach (var size in availableSizes)
                {
                    if (size.Chance >= UtilitiesFunctions.RandomDouble())
                    {
                        rare = size.Size == availableSizes.Max(x => x.Size);
                        newSize = (short)(size.Size * 100);
                        changeSize = true;
                        break;
                    }
                }
            }

            client.Tamer.Inventory.RemoveOrReduceItem(targetItem, 1);

            _logger.Verbose("Character {TamerId} used {ItemId} to change partner {PartnerId} size from {Old}% to {New}%.",
                client.TamerId, targetItem.ItemId, client.Partner.Id, client.Partner.Size / 100, newSize / 100);

            client.Partner.SetSize(newSize);
            if (client.DungeonMap)
                _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, new UpdateSizePacket(client.Partner.GeneralHandler, client.Partner.Size).Serialize());
            else
                _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new UpdateSizePacket(client.Partner.GeneralHandler, client.Partner.Size).Serialize());

            client.Partner.SetBaseStatus(_statusManager.GetDigimonBaseStatus(client.Partner.CurrentType, client.Partner.Level, client.Partner.Size));

            if (rare)
            {
                _mapServer.BroadcastGlobal(new NeonMessagePacket(NeonMessageTypeEnum.Scale, client.Tamer.Name, client.Partner.BaseType, client.Partner.Size).Serialize());
                _dungeonServer.BroadcastGlobal(new NeonMessagePacket(NeonMessageTypeEnum.Scale, client.Tamer.Name, client.Partner.BaseType, client.Partner.Size).Serialize());
            }

            await _sender.Send(new UpdateItemCommand(targetItem));
            await _sender.Send(new UpdateDigimonSizeCommand(client.Partner.Id, client.Partner.Size));

            client.Send(UtilitiesFunctions.GroupPackets(
                new ItemConsumeSuccessPacket(client.Tamer.GeneralHandler, itemSlot).Serialize(),
                new UpdateStatusPacket(client.Tamer).Serialize(),
                new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
            ));
        }

        private async Task Transcend(GameClient client, short itemSlot, ItemModel targetItem)
        {
            var digimonGrade = client.Partner.HatchGrade;
            var digimonSize = client.Partner.Size;
            var evolutionType = _assets.DigimonBaseInfo.First(x => x.Type == client.Partner.CurrentType).EvolutionType;

            if (digimonGrade == DigimonHatchGradeEnum.Transcend)
            {
                client.Send(new SystemMessagePacket("Seu Digimon ja esta transcendido !!"));
                client.Send(UtilitiesFunctions.GroupPackets(
                    new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type).Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                ));
                return;
            }
            else if (digimonGrade == DigimonHatchGradeEnum.Perfect && digimonSize >= 12500 && (EvolutionRankEnum)evolutionType != EvolutionRankEnum.Spirit)
            {
                var random = new Random();
                int randomSize = random.Next(12500, 13901);

                client.Partner.Transcend();
                client.Partner.SetSize((short)randomSize);
                client.Partner.SetBaseStatus(_statusManager.GetDigimonBaseStatus(client.Partner.CurrentType, client.Partner.Level, client.Partner.Size));

                client.Tamer.Inventory.RemoveOrReduceItem(targetItem, 1);

                await _sender.Send(new UpdateItemCommand(targetItem));
                await _sender.Send(new UpdateDigimonSizeCommand(client.Partner.Id, client.Partner.Size));
                await _sender.Send(new UpdateDigimonGradeCommand(client.Partner.Id, client.Partner.HatchGrade));

                client.Send(UtilitiesFunctions.GroupPackets(
                    new ItemConsumeSuccessPacket(client.Tamer.GeneralHandler, itemSlot).Serialize(),
                    new UpdateStatusPacket(client.Tamer).Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                ));

                client.Tamer.UpdateState(CharacterStateEnum.Loading);
                await _sender.Send(new UpdateCharacterStateCommand(client.TamerId, CharacterStateEnum.Loading));

                _mapServer.RemoveClient(client);

                client.SetGameQuit(false);
                client.Tamer.UpdateSlots();

                client.Send(new MapSwapPacket(_configuration[GamerServerPublic], _configuration[GameServerPort],
                    client.Tamer.Location.MapId, client.Tamer.Location.X, client.Tamer.Location.Y));

                client.Send(new SystemMessagePacket($"Digimon {client.Partner.Name} transcendido com tamanho {randomSize}!!"));
            }
            else if ((EvolutionRankEnum)evolutionType == EvolutionRankEnum.Spirit && digimonSize >= 12500)
            {
                var random = new Random();
                int randomSize = random.Next(12500, 13901);

                client.Partner.Transcend();
                client.Partner.SetSize((short)randomSize);
                client.Partner.SetBaseStatus(_statusManager.GetDigimonBaseStatus(client.Partner.CurrentType, client.Partner.Level, client.Partner.Size));

                client.Tamer.Inventory.RemoveOrReduceItem(targetItem, 1);

                await _sender.Send(new UpdateItemCommand(targetItem));
                await _sender.Send(new UpdateDigimonSizeCommand(client.Partner.Id, client.Partner.Size));
                await _sender.Send(new UpdateDigimonGradeCommand(client.Partner.Id, client.Partner.HatchGrade));

                client.Send(UtilitiesFunctions.GroupPackets(
                    new ItemConsumeSuccessPacket(client.Tamer.GeneralHandler, itemSlot).Serialize(),
                    new UpdateStatusPacket(client.Tamer).Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                ));

                client.Tamer.UpdateState(CharacterStateEnum.Loading);
                await _sender.Send(new UpdateCharacterStateCommand(client.TamerId, CharacterStateEnum.Loading));

                _mapServer.RemoveClient(client);

                client.SetGameQuit(false);
                client.Tamer.UpdateSlots();

                client.Send(new MapSwapPacket(_configuration[GamerServerPublic], _configuration[GameServerPort],
                    client.Tamer.Location.MapId, client.Tamer.Location.X, client.Tamer.Location.Y));

                client.Send(new SystemMessagePacket($"Digimon {client.Partner.Name} transcendido com tamanho {randomSize}!!"));
            }
            else
            {
                client.Send(new SystemMessagePacket("Seu digimon nao possui os requerimentos para transcender !!"));
                client.Send(new SystemMessagePacket("Digite (!transcend help) para mais informacoes.", ""));
                client.Send(UtilitiesFunctions.GroupPackets(
                    new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type).Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                ));
                return;
            }
        }

        private async Task ConsumeFoodItem(GameClient client, short itemSlot, ItemModel targetItem)
        {
            if (targetItem.ItemInfo?.SkillInfo == null)
            {
                client.Send(UtilitiesFunctions.GroupPackets(
                    new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type).Serialize(),
                    new SystemMessagePacket($"Invalid skill info for item id {targetItem.ItemId}.").Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                ));
                _logger.Warning("FoodItem: invalid skill info for item {ItemId} (tamer {TamerId}).", targetItem.ItemId, client.TamerId);
                return;
            }

            try
            {
                // Flags para enviar updates no final (evita duplicados e cobre todos os casos)
                bool tamerHpChanged = false, tamerDsChanged = false;
                bool partnerHpChanged = false, partnerDsChanged = false;

                foreach (var apply in targetItem.ItemInfo.SkillInfo.Apply)
                {
                    switch (apply.Type)
                    {
                        case SkillCodeApplyTypeEnum.Percent:
                        case SkillCodeApplyTypeEnum.AlsoPercent:
                            {
                                // Valor em % do HP/DS Máximo
                                switch (apply.Attribute)
                                {
                                    case SkillCodeApplyAttributeEnum.HP:
                                        switch (targetItem.ItemInfo.Target)
                                        {
                                            case ItemConsumeTargetEnum.Both:
                                                client.Tamer.RecoverHp((int)Math.Ceiling((double)apply.Value / 100 * client.Tamer.HP));
                                                client.Partner.RecoverHp((int)Math.Ceiling((double)apply.Value / 100 * client.Partner.HP));
                                                tamerHpChanged = true; partnerHpChanged = true;
                                                break;
                                            case ItemConsumeTargetEnum.Digimon:
                                                client.Partner.RecoverHp((int)Math.Ceiling((double)apply.Value / 100 * client.Partner.HP));
                                                partnerHpChanged = true;
                                                break;
                                            case ItemConsumeTargetEnum.Tamer:
                                                client.Tamer.RecoverHp((int)Math.Ceiling((double)apply.Value / 100 * client.Tamer.HP));
                                                tamerHpChanged = true;
                                                break;
                                        }
                                        break;

                                    case SkillCodeApplyAttributeEnum.DS:
                                        switch (targetItem.ItemInfo.Target)
                                        {
                                            case ItemConsumeTargetEnum.Both:
                                                client.Tamer.RecoverDs((int)Math.Ceiling((double)apply.Value / 100 * client.Tamer.DS));
                                                client.Partner.RecoverDs((int)Math.Ceiling((double)apply.Value / 100 * client.Partner.DS));
                                                tamerDsChanged = true; partnerDsChanged = true;
                                                break;
                                            case ItemConsumeTargetEnum.Digimon:
                                                client.Partner.RecoverDs((int)Math.Ceiling((double)apply.Value / 100 * client.Partner.DS));
                                                partnerDsChanged = true;
                                                break;
                                            case ItemConsumeTargetEnum.Tamer:
                                                client.Tamer.RecoverDs((int)Math.Ceiling((double)apply.Value / 100 * client.Tamer.DS));
                                                tamerDsChanged = true;
                                                break;
                                        }
                                        break;
                                }
                                break;
                            }

                        case SkillCodeApplyTypeEnum.Default:
                            {
                                // Valor absoluto
                                switch (apply.Attribute)
                                {
                                    case SkillCodeApplyAttributeEnum.HP:
                                        switch (targetItem.ItemInfo.Target)
                                        {
                                            case ItemConsumeTargetEnum.Both:
                                                client.Tamer.RecoverHp(apply.Value);
                                                client.Partner.RecoverHp(apply.Value);
                                                tamerHpChanged = true; partnerHpChanged = true;
                                                break;
                                            case ItemConsumeTargetEnum.Digimon:
                                                client.Partner.RecoverHp(apply.Value);
                                                partnerHpChanged = true;
                                                break;
                                            case ItemConsumeTargetEnum.Tamer:
                                                client.Tamer.RecoverHp(apply.Value);
                                                tamerHpChanged = true;
                                                break;
                                        }
                                        break;

                                    case SkillCodeApplyAttributeEnum.DS:
                                        switch (targetItem.ItemInfo.Target)
                                        {
                                            case ItemConsumeTargetEnum.Both:
                                                client.Tamer.RecoverDs(apply.Value);
                                                client.Partner.RecoverDs(apply.Value);
                                                tamerDsChanged = true; partnerDsChanged = true;
                                                break;
                                            case ItemConsumeTargetEnum.Digimon:
                                                client.Partner.RecoverDs(apply.Value);
                                                partnerDsChanged = true;
                                                break;
                                            case ItemConsumeTargetEnum.Tamer:
                                                client.Tamer.RecoverDs(apply.Value);
                                                tamerDsChanged = true;
                                                break;
                                        }
                                        break;

                                    case SkillCodeApplyAttributeEnum.XG:
                                        if (targetItem.ItemInfo.Target == ItemConsumeTargetEnum.Both || targetItem.ItemInfo.Target == ItemConsumeTargetEnum.Tamer)
                                        {
                                            client.Tamer.SetXGauge(apply.Value);
                                            client.Send(new TamerXaiResourcesPacket(client.Tamer.XGauge, client.Tamer.XCrystals));
                                        }
                                        break;
                                }
                                break;
                            }
                    }
                }

                // ---- Sempre enviar os updates após aplicar os efeitos (cobre mundo aberto e dungeons) ----
                // Pacote de recursos (HP/DS numérico) vai apenas para o próprio cliente
                if (tamerHpChanged || tamerDsChanged)
                    client.Send(new UpdateCurrentResourcesPacket(client.Tamer.GeneralHandler, (short)client.Tamer.CurrentHp, (short)client.Tamer.CurrentDs, 0));
                if (partnerHpChanged || partnerDsChanged)
                    client.Send(new UpdateCurrentResourcesPacket(client.Partner.GeneralHandler, (short)client.Partner.CurrentHp, (short)client.Partner.CurrentDs, 0));

                // Broadcast das "HP rates" para quem vê o jogador (aqui diferencia dungeon vs. mapa normal)
                if (client.DungeonMap)
                {
                    if (tamerHpChanged)
                        _dungeonServer.BroadcastForTargetTamers(client.TamerId, new UpdateCurrentHPRatePacket(client.Tamer.GeneralHandler, client.Tamer.HpRate).Serialize());
                    if (partnerHpChanged)
                        _dungeonServer.BroadcastForTargetTamers(client.TamerId, new UpdateCurrentHPRatePacket(client.Partner.GeneralHandler, client.Partner.HpRate).Serialize());
                }
                else
                {
                    if (tamerHpChanged)
                        _mapServer.BroadcastForTargetTamers(client.TamerId, new UpdateCurrentHPRatePacket(client.Tamer.GeneralHandler, client.Tamer.HpRate).Serialize());
                    if (partnerHpChanged)
                        _mapServer.BroadcastForTargetTamers(client.TamerId, new UpdateCurrentHPRatePacket(client.Partner.GeneralHandler, client.Partner.HpRate).Serialize());
                }

                _logger.Verbose("Character {TamerId} consumed {ItemId}.", client.TamerId, targetItem.ItemId);

                // Persistência/consumo do item
                client.Tamer.Inventory.RemoveOrReduceItem(targetItem, 1, itemSlot);
                await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));

                // Feedback UI
                client.Send(UtilitiesFunctions.GroupPackets(
                    new ItemConsumeSuccessPacket(client.Tamer.GeneralHandler, itemSlot).Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                ));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "FoodItem: exception consuming item {ItemId} (tamer {TamerId})", targetItem.ItemId, client.TamerId);
                client.Send(UtilitiesFunctions.GroupPackets(
                    new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type).Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                ));
            }
        }

        private async Task IncreaseArchiveSlots(GameClient client, short itemSlot, ItemModel targetItem)
        {
            client.Tamer.DigimonArchive.AddSlot();

            _logger.Verbose($"Character {client.TamerId} used {targetItem.ItemId} to expand digimon archive slots to {client.Tamer.DigimonArchive.Slots}.");

            client.Tamer.Inventory.RemoveOrReduceItem(targetItem, 1);

            await _sender.Send(new UpdateItemCommand(targetItem));
            await _sender.Send(new CreateCharacterDigimonArchiveSlotCommand(
                    client.Tamer.DigimonArchive.DigimonArchives.Last(),
                    client.Tamer.DigimonArchive.Id
                )
            );

            client.Send(
                UtilitiesFunctions.GroupPackets(
                    new ItemConsumeSuccessPacket(client.Tamer.GeneralHandler, itemSlot).Serialize(),
                    new DigimonArchiveLoadPacket(client.Tamer.DigimonArchive).Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                )
            );
        }

        private async Task IncreaseDigimonSlots(GameClient client, short itemSlot, ItemModel targetItem)
        {
            client.Tamer.AddDigimonSlots();

            _logger.Verbose($"Character {client.TamerId} used {targetItem.ItemId} to expand digimon slots to {client.Tamer.DigimonSlots}.");

            client.Tamer.Inventory.RemoveOrReduceItem(targetItem, 1);

            await _sender.Send(new UpdateCharacterDigimonSlotsCommand(client.Tamer.Id, client.Tamer.DigimonSlots));
            await _sender.Send(new UpdateItemCommand(targetItem));

            client.Send(
                UtilitiesFunctions.GroupPackets(
                    new ItemConsumeSuccessPacket(client.Tamer.GeneralHandler, itemSlot).Serialize(),
                    new UpdateDigimonSlotsPacket(client.Tamer.DigimonSlots).Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                )
            );
        }

        private async Task IncreaseWarehouseSlots(GameClient client, short itemSlot, ItemModel targetItem)
        {
            var newSlot = client.Tamer.Warehouse.AddSlot();

            _logger.Verbose($"Character {client.TamerId} used {targetItem.ItemId} to expand warehouse slots to {client.Tamer.Warehouse.Size}.");

            client.Tamer.Inventory.RemoveOrReduceItem(targetItem, 1);

            await _sender.Send(new UpdateItemCommand(targetItem));
            await _sender.Send(new AddInventorySlotCommand(newSlot));

            client.Send(
                UtilitiesFunctions.GroupPackets(
                    new ItemConsumeSuccessPacket(client.Tamer.GeneralHandler, itemSlot).Serialize(),
                    new LoadInventoryPacket(client.Tamer.Warehouse, InventoryTypeEnum.Warehouse).Serialize()
                )
            );
        }

        private async Task IncreaseInventorySlots(GameClient client, short itemSlot, ItemModel targetItem)
        {
            var newSlot = client.Tamer.Inventory.AddSlot();

            _logger.Verbose($"Character {client.TamerId} used {targetItem.ItemId} to expand inventory slots to {client.Tamer.Inventory.Size}.");

            client.Tamer.Inventory.RemoveOrReduceItem(targetItem, 1);

            await _sender.Send(new UpdateItemCommand(targetItem));
            await _sender.Send(new AddInventorySlotCommand(newSlot));

            client.Send(
                UtilitiesFunctions.GroupPackets(
                    new ItemConsumeSuccessPacket(client.Tamer.GeneralHandler, itemSlot).Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                )
            );
        }

        private async Task ContainerItem(GameClient client, short itemSlot, ItemModel targetItem)
        {
            var containerItem = client.Tamer.Inventory.FindItemBySlot(itemSlot);
            var ItemId = 0;

            if (containerItem == null || containerItem.ItemId == 0 || containerItem.ItemInfo == null)
            {
                client.Send(UtilitiesFunctions.GroupPackets(
                    new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type).Serialize(),
                    new SystemMessagePacket($"Invalid item on slot {itemSlot} for tamer {client.TamerId}").Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                ));
                _logger.Warning("ContainerItem: invalid item on slot {Slot} (tamer {TamerId}).", itemSlot, client.TamerId);
                return;
            }

            var containerAsset = _assets.Container.FirstOrDefault(x => x.ItemId == containerItem.ItemId);
            if (containerAsset == null)
            {
                client.Send(UtilitiesFunctions.GroupPackets(
                    new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type).Serialize(),
                    new SystemMessagePacket($"No container configuration for item id {containerItem.ItemId}.").Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                ));
                _logger.Warning("ContainerItem: no config for item {ItemId}.", containerItem.ItemId);
                return;
            }

            if (!containerAsset.Rewards.Any())
            {
                client.Send(UtilitiesFunctions.GroupPackets(
                    new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type).Serialize(),
                    new SystemMessagePacket($"Container config for item {containerAsset.ItemId} has incorrect rewards configuration.").Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                ));
                _logger.Warning("ContainerItem: empty rewards for item {ItemId}.", containerAsset.ItemId);
                return;
            }

            var receivedItems = new List<ItemModel>();
            var possibleRewards = containerAsset.Rewards.OrderBy(x => Guid.NewGuid()).ToList();
            var rewardsToReceive = containerAsset.RewardAmount;
            var receivedRewardsAmount = 0;
            var error = false;

            ItemId = containerItem.ItemId;

            var needChance = rewardsToReceive < possibleRewards.Count;

            while (receivedRewardsAmount < rewardsToReceive && !error)
            {
                foreach (var possibleReward in possibleRewards)
                {
                    if (needChance && possibleReward.Chance < UtilitiesFunctions.RandomDouble())
                        continue;

                    var contentItem = new ItemModel();
                    contentItem.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == possibleReward.ItemId));

                    if (contentItem.ItemInfo == null)
                    {
                        client.Send(new SystemMessagePacket($"Invalid item info for item {possibleReward.ItemId}."));
                        _logger.Warning("ContainerItem: invalid item info for item {ItemId} (tamer {TamerId}).", possibleReward.ItemId, client.TamerId);
                        error = true;
                        return;
                    }

                    contentItem.SetItemId(possibleReward.ItemId);
                    contentItem.SetAmount(UtilitiesFunctions.RandomInt(possibleReward.MinAmount, possibleReward.MaxAmount));

                    if (contentItem.IsTemporary)
                        contentItem.SetRemainingTime((uint)contentItem.ItemInfo.UsageTimeMinutes);

                    var tempItem = (ItemModel)contentItem.Clone();
                    receivedItems.Add(tempItem);
                    receivedRewardsAmount++;

                    if (receivedRewardsAmount >= rewardsToReceive || error)
                        break;
                }
            }

            if (error)
            {
                client.Send(UtilitiesFunctions.GroupPackets(
                    new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type).Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                ));
            }
            else
            {
                var receiveList = string.Join(',', receivedItems.Select(x => $"{x.ItemId} x{x.Amount}"));
                _logger.Verbose("Character {TamerId} openned box {ItemId} and obtained {List}", client.TamerId, containerItem.ItemId, receiveList);

                client.Tamer.Inventory.RemoveOrReduceItem(containerItem, 1, itemSlot);

                client.Send(UtilitiesFunctions.GroupPackets(
                    new ItemConsumeSuccessPacket(client.Tamer.GeneralHandler, itemSlot).Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                ));

                receivedItems.ForEach(receivedItem =>
                {
                    client.Tamer.Inventory.AddItem(receivedItem);
                    client.Send(new ReceiveItemPacket(receivedItem, InventoryTypeEnum.Inventory));
                });

                await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));

                if (ItemId == 70102) // TODO: parametrizar
                {
                    int time = 90 * 24 * 3600;      // 90 days membership
                    client.IncreaseMembershipDuration(time);
                    await _sender.Send(new UpdateAccountMembershipCommand(client.AccountId, client.MembershipExpirationDate));

                    var buff = _assets.BuffInfo.Where(x => x.BuffId == 50121 || x.BuffId == 50122 || x.BuffId == 50123).ToList();
                    int duration = client.MembershipUtcSecondsBuff;

                    buff.ForEach(buffAsset =>
                    {
                        if (!client.Tamer.BuffList.Buffs.Any(x => x.BuffId == buffAsset.BuffId))
                        {
                            var newCharacterBuff = CharacterBuffModel.Create(buffAsset.BuffId, buffAsset.SkillId, 2592000, duration);
                            newCharacterBuff.SetBuffInfo(buffAsset);

                            client.Tamer.BuffList.Buffs.Add(newCharacterBuff);
                            _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new AddBuffPacket(client.Tamer.GeneralHandler, buffAsset, 0, duration).Serialize());
                        }
                        else
                        {
                            var buffInfo = client.Tamer.BuffList.Buffs.First(x => x.BuffId == buffAsset.BuffId);
                            if (buffInfo != null)
                            {
                                buffInfo.SetDuration(duration, true);
                                _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new UpdateBuffPacket(client.Tamer.GeneralHandler, buffAsset, 0, duration).Serialize());
                            }
                        }
                    });

                    client.Send(new MembershipPacket(client.MembershipExpirationDate!.Value, duration));
                    client.Send(new UpdateStatusPacket(client.Tamer));

                    await _sender.Send(new UpdateCharacterBuffListCommand(client.Tamer.BuffList));

                    client.Tamer.UpdateState(CharacterStateEnum.Loading);
                    await _sender.Send(new UpdateCharacterStateCommand(client.TamerId, CharacterStateEnum.Loading));

                    _mapServer.RemoveClient(client);

                    client.SetGameQuit(false);
                    client.Tamer.UpdateSlots();

                    client.Send(new MapSwapPacket(_configuration[GamerServerPublic], _configuration[GameServerPort],
                        client.Tamer.Location.MapId, client.Tamer.Location.X, client.Tamer.Location.Y));
                }
            }
        }

        private async Task BuffItem(GameClient client, short itemSlot, ItemModel targetItem)
        {
            var buff = _assets.BuffInfo.FirstOrDefault(x => x.SkillCode == targetItem.ItemInfo.SkillCode);

            if (buff != null)
            {
                var duration = UtilitiesFunctions.RemainingTimeSeconds(targetItem.ItemInfo.TimeInSeconds);

                var newCharacterBuff = CharacterBuffModel.Create(buff.BuffId, buff.SkillId, targetItem.ItemInfo.TypeN, targetItem.ItemInfo.TimeInSeconds);
                newCharacterBuff.SetBuffInfo(buff);

                var newDigimonBuff = DigimonBuffModel.Create(buff.BuffId, buff.SkillId, targetItem.ItemInfo.TypeN, targetItem.ItemInfo.TimeInSeconds);
                newDigimonBuff.SetBuffInfo(buff);

                var characterBuffs = new List<SkillCodeApplyAttributeEnum>
                {
                    SkillCodeApplyAttributeEnum.MS,
                    SkillCodeApplyAttributeEnum.MovementSpeedIncrease,
                    SkillCodeApplyAttributeEnum.EXP,
                    SkillCodeApplyAttributeEnum.AttributeExperienceAdded
                };

                if (characterBuffs.Contains(buff.SkillInfo.Apply.First().Attribute))
                {
                    if (client.Tamer.BuffList.ActiveBuffs.Any(x => x.BuffId == buff.BuffId))
                    {
                        if (client.DungeonMap)
                        {
                            client.Tamer.BuffList.ForceExpired(newCharacterBuff.BuffId);
                            _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, new RemoveBuffPacket(client.Tamer.GeneralHandler, newCharacterBuff.BuffId).Serialize());

                            client.Tamer.BuffList.Add(newCharacterBuff);
                            _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, new AddBuffPacket(client.Tamer.GeneralHandler, buff, (short)targetItem.ItemInfo.TypeN, duration).Serialize());
                        }
                        else
                        {
                            client.Tamer.BuffList.ForceExpired(newCharacterBuff.BuffId);
                            _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new RemoveBuffPacket(client.Tamer.GeneralHandler, newCharacterBuff.BuffId).Serialize());

                            client.Tamer.BuffList.Add(newCharacterBuff);
                            _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new AddBuffPacket(client.Tamer.GeneralHandler, buff, (short)targetItem.ItemInfo.TypeN, duration).Serialize());
                        }
                    }
                    else
                    {
                        if (client.DungeonMap)
                        {
                            client.Tamer.BuffList.Add(newCharacterBuff);
                            _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, new AddBuffPacket(client.Tamer.GeneralHandler, buff, (short)targetItem.ItemInfo.TypeN, duration).Serialize());
                        }
                        else
                        {
                            client.Tamer.BuffList.Add(newCharacterBuff);
                            _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new AddBuffPacket(client.Tamer.GeneralHandler, buff, (short)targetItem.ItemInfo.TypeN, duration).Serialize());
                        }
                    }
                }
                else
                {
                    if (client.Partner.BuffList.ActiveBuffs.Any(x => x.BuffId == buff.BuffId))
                    {
                        if (client.DungeonMap)
                        {
                            client.Partner.BuffList.ForceExpired(newDigimonBuff.BuffId);
                            _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, new RemoveBuffPacket(client.Partner.GeneralHandler, newCharacterBuff.BuffId).Serialize());

                            client.Partner.BuffList.Add(newDigimonBuff);
                            _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, new AddBuffPacket(client.Partner.GeneralHandler, buff, (short)targetItem.ItemInfo.TypeN, duration).Serialize());
                        }
                        else
                        {
                            client.Partner.BuffList.ForceExpired(newDigimonBuff.BuffId);
                            _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new RemoveBuffPacket(client.Partner.GeneralHandler, newCharacterBuff.BuffId).Serialize());

                            client.Partner.BuffList.Add(newDigimonBuff);
                            _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new AddBuffPacket(client.Partner.GeneralHandler, buff, (short)targetItem.ItemInfo.TypeN, duration).Serialize());
                        }
                    }
                    else
                    {
                        if (client.DungeonMap)
                        {
                            client.Partner.BuffList.Add(newDigimonBuff);
                            _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, new AddBuffPacket(client.Partner.GeneralHandler, buff, (short)targetItem.ItemInfo.TypeN, duration).Serialize());
                        }
                        else
                        {
                            client.Partner.BuffList.Add(newDigimonBuff);
                            _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new AddBuffPacket(client.Partner.GeneralHandler, buff, (short)targetItem.ItemInfo.TypeN, duration).Serialize());
                        }
                    }
                }

                _logger.Verbose("Character {TamerId} consumed {ItemId} to get buff.", client.TamerId, targetItem.ItemId);

                client.Tamer.Inventory.RemoveOrReduceItem(targetItem, 1);
                await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
                await _sender.Send(new UpdateCharacterBuffListCommand(client.Tamer.BuffList));
                await _sender.Send(new UpdateDigimonBuffListCommand(client.Partner.BuffList));

                client.Send(UtilitiesFunctions.GroupPackets(
                    new ItemConsumeSuccessPacket(client.Tamer.GeneralHandler, itemSlot).Serialize(),
                    new UpdateStatusPacket(client.Tamer).Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                ));
            }
            else
            {
                client.Send(UtilitiesFunctions.GroupPackets(
                    new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type).Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                ));
            }
        }

        private async Task ConsumeExpItem(GameClient client, short itemSlot, ItemModel targetItem)
        {
            if (targetItem.ItemInfo?.SkillInfo == null)
            {
                client.Send(UtilitiesFunctions.GroupPackets(
                    new ItemConsumeFailPacket(itemSlot, targetItem.ItemInfo.Type).Serialize(),
                    new SystemMessagePacket($"Invalid skill info for item id {targetItem.ItemId}.").Serialize(),
                    new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
                ));
                _logger.Error("ExpItem: invalid skill info for item {ItemId} (tamer {TamerId}).", targetItem.ItemId, client.TamerId);
                return;
            }

            foreach (var apply in targetItem.ItemInfo.SkillInfo.Apply)
            {
                switch (apply.Type)
                {
                    case SkillCodeApplyTypeEnum.None:
                        break;

                    case SkillCodeApplyTypeEnum.Default:
                        {
                            if (apply.Attribute != SkillCodeApplyAttributeEnum.EXP)
                                break;

                            switch (targetItem.ItemInfo.Target)
                            {
                                case ItemConsumeTargetEnum.Both:
                                    {
                                        var random = new Random();
                                        int randomValue = random.Next(targetItem.ItemInfo.ApplyValueMin, targetItem.ItemInfo.ApplyValueMax + 1);
                                        int value = apply.Value * (randomValue / 100);

                                        var result = _expManager.ReceiveTamerExperience(value, client.Tamer);
                                        var result2 = _expManager.ReceiveDigimonExperience(value, client.Tamer.Partner);

                                        if (result.Success)
                                        {
                                            client.Send(new ReceiveExpPacket(
                                                value, 0, client.Tamer.CurrentExperience,
                                                client.Tamer.Partner.GeneralHandler, 0, 0, client.Tamer.Partner.CurrentExperience, 0));
                                        }
                                        else
                                        {
                                            client.Send(new SystemMessagePacket($"No proper configuration for tamer {client.Tamer.Model} leveling."));
                                            return;
                                        }

                                        if (result.LevelGain > 0)
                                        {
                                            client.Tamer.SetLevelStatus(_statusManager.GetTamerLevelStatus(client.Tamer.Model, client.Tamer.Level));
                                            BroadcastLevelUp(client, client.Tamer.GeneralHandler, client.Tamer.Level);
                                            client.Tamer.FullHeal();
                                            client.Send(new UpdateStatusPacket(client.Tamer));
                                        }

                                        if (result.Success)
                                            await _sender.Send(new UpdateCharacterExperienceCommand(client.TamerId, client.Tamer.CurrentExperience, client.Tamer.Level));

                                        if (result2.Success)
                                        {
                                            client.Send(new ReceiveExpPacket(
                                                0, 0, client.Tamer.CurrentExperience,
                                                client.Tamer.Partner.GeneralHandler, value, 0, client.Tamer.Partner.CurrentExperience, 0));
                                        }

                                        if (result2.LevelGain > 0)
                                        {
                                            client.Partner.SetBaseStatus(_statusManager.GetDigimonBaseStatus(client.Partner.CurrentType, client.Partner.Level, client.Partner.Size));
                                            BroadcastLevelUp(client, client.Partner.GeneralHandler, client.Partner.Level);
                                            client.Partner.FullHeal();
                                            client.Send(new UpdateStatusPacket(client.Tamer));
                                        }

                                        if (result2.Success)
                                            await _sender.Send(new UpdateDigimonExperienceCommand(client.Partner));
                                        break;
                                    }

                                case ItemConsumeTargetEnum.Digimon:
                                    {
                                        var random = new Random();
                                        int randomValue = random.Next(targetItem.ItemInfo.ApplyValueMin, targetItem.ItemInfo.ApplyValueMax + 1);
                                        int value = apply.Value * (randomValue / 100);

                                        var digimonResult = _expManager.ReceiveDigimonExperience(value, client.Tamer.Partner);

                                        if (digimonResult.Success)
                                        {
                                            client.Send(new ReceiveExpPacket(
                                                0, 0, client.Tamer.CurrentExperience,
                                                client.Tamer.Partner.GeneralHandler, value, 0, client.Tamer.Partner.CurrentExperience, 0));
                                        }

                                        if (digimonResult.LevelGain > 0)
                                        {
                                            client.Partner.SetBaseStatus(_statusManager.GetDigimonBaseStatus(client.Partner.CurrentType, client.Partner.Level, client.Partner.Size));
                                            BroadcastLevelUp(client, client.Partner.GeneralHandler, client.Partner.Level);
                                            client.Partner.FullHeal();
                                            client.Send(new UpdateStatusPacket(client.Tamer));
                                        }

                                        if (digimonResult.Success)
                                            await _sender.Send(new UpdateDigimonExperienceCommand(client.Partner));
                                        break;
                                    }

                                case ItemConsumeTargetEnum.Tamer:
                                    {
                                        var random = new Random();
                                        int randomValue = random.Next(targetItem.ItemInfo.ApplyValueMin, targetItem.ItemInfo.ApplyValueMax + 1);
                                        int value = apply.Value * (randomValue / 100);

                                        var result = _expManager.ReceiveTamerExperience(value, client.Tamer);

                                        if (result.Success)
                                        {
                                            client.Send(new ReceiveExpPacket(
                                                value, 0, client.Tamer.CurrentExperience,
                                                client.Tamer.Partner.GeneralHandler, 0, 0, client.Tamer.Partner.CurrentExperience, 0));
                                        }
                                        else
                                        {
                                            client.Send(new SystemMessagePacket($"No proper configuration for tamer {client.Tamer.Model} leveling."));
                                            return;
                                        }

                                        if (result.LevelGain > 0)
                                        {
                                            client.Tamer.SetLevelStatus(_statusManager.GetTamerLevelStatus(client.Tamer.Model, client.Tamer.Level));
                                            _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new LevelUpPacket(client.Tamer.GeneralHandler, client.Tamer.Level).Serialize());
                                            client.Tamer.FullHeal();
                                            client.Send(new UpdateStatusPacket(client.Tamer));
                                        }

                                        if (result.Success)
                                            await _sender.Send(new UpdateCharacterExperienceCommand(client.TamerId, client.Tamer.CurrentExperience, client.Tamer.Level));
                                        break;
                                    }
                            }
                            break;
                        }

                    default:
                        _logger.Error("ApplyType: {Type} not configured! (ItemConsumePacket)", apply.Type);
                        return;
                }
            }

            client.Tamer.Inventory.RemoveOrReduceItem(targetItem, 1, itemSlot);
            await _sender.Send(new UpdateItemCommand(targetItem));
            await _sender.Send(new UpdateCharacterBasicInfoCommand(client.Tamer));

            client.Send(UtilitiesFunctions.GroupPackets(
                new ItemConsumeSuccessPacket(client.Tamer.GeneralHandler, itemSlot).Serialize(),
                new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize()
            ));

            _logger.Verbose("Tamer {Name} consumed 1 : {ItemName}", client.Tamer.Name, targetItem.ItemInfo.Name);
        }
    }
}
