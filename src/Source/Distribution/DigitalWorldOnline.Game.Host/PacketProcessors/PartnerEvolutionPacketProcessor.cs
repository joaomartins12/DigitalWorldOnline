using DigitalWorldOnline.Application;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models;
using DigitalWorldOnline.Commons.Models.Character;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Packets.GameServer.Combat;
using DigitalWorldOnline.Commons.Packets.Items;
using DigitalWorldOnline.Commons.Utils;
using DigitalWorldOnline.Game.Managers;
using DigitalWorldOnline.GameHost;
using MediatR;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class PartnerEvolutionPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.PartnerEvolution;

        private readonly PartyManager _partyManager;
        private readonly StatusManager _statusManager;
        private readonly AssetsLoader _assets;
        private readonly MapServer _mapServer;
        private readonly DungeonsServer _dungeonServer;
        private readonly ISender _sender;
        private readonly ILogger _logger;

        public PartnerEvolutionPacketProcessor(
            PartyManager partyManager,
            StatusManager statusManager,
            AssetsLoader assets,
            MapServer mapServer,
            ISender sender,
            ILogger logger,
            DungeonsServer dungeonServer)
        {
            _partyManager = partyManager;
            _statusManager = statusManager;
            _assets = assets;
            _mapServer = mapServer;
            _sender = sender;
            _logger = logger;
            _dungeonServer = dungeonServer;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);

            var digimonHandle = packet.ReadInt();
            var evoStage = packet.ReadByte();

            if (client.Partner == null)
            {
                client.Send(new DigimonEvolutionFailPacket());
                return;
            }

            var evoInfoRoot = _assets.EvolutionInfo.FirstOrDefault(x => x.Type == client.Partner.BaseType);
            var currentLine = evoInfoRoot?.Lines.FirstOrDefault(x => x.Type == client.Partner.CurrentType);
            var evoLine = currentLine?.Stages;

            if (evoLine == null || evoLine.Count == 0)
            {
                client.Send(new DigimonEvolutionFailPacket());
                return;
            }

            // guarda de índice
            if (evoStage >= evoLine.Count)
            {
                _logger.Warning("PartnerEvolution: evoStage fora do intervalo (stage={Stage}, count={Count})", evoStage, evoLine.Count);
                client.Send(new DigimonEvolutionFailPacket());
                return;
            }

            var targetStage = evoLine[(int)evoStage];
            if (targetStage == null)
            {
                client.Send(new DigimonEvolutionFailPacket());
                return;
            }

            var targetInfo = evoInfoRoot?.Lines.FirstOrDefault(x => x.Type == targetStage.Type);
            if (targetInfo == null)
            {
                _logger.Error("PartnerEvolution: targetInfo não encontrado para Type={Type}", targetStage.Type);
                client.Send(new DigimonEvolutionFailPacket());
                return;
            }

            var starterPartners = new List<int> { 31001, 31002, 31003, 31004 };
            if (!client.Partner.BaseType.IsBetween(starterPartners.ToArray()))
            {
                var targetEvo = client.Partner.Evolutions.FirstOrDefault(x => x.Type == targetStage.Type);
                if (targetEvo == null || targetEvo.Unlocked == 0)
                {
                    _logger.Verbose("Tamer {Name} tentou evoluir {Id}:{Base} para {Target} sem desbloquear.",
                        client.Tamer.Name, client.Partner.Id, client.Partner.BaseInfo?.Name, targetEvo?.Type);
                    client.Send(new DigimonEvolutionFailPacket());
                    return;
                }
            }
            else
            {
                var targetEvo = client.Partner.Evolutions.FirstOrDefault(x => x.Type == targetStage.Type);
                if (targetInfo.SlotLevel > 4 && (targetEvo == null || targetEvo.Unlocked == 0))
                {
                    _logger.Verbose("Tamer {Name} tentou evoluir starter {Id}:{Base} para {Target} sem desbloquear.",
                        client.Tamer.Name, client.Partner.Id, client.Partner.BaseInfo?.Name, targetEvo?.Type);
                    client.Send(new DigimonEvolutionFailPacket());
                    return;
                }
            }

            // -- remove buff passivo do parceiro antes de evoluir
            var buffToRemove = client.Tamer.Partner.BuffList.TamerBaseSkill();
            if (buffToRemove != null)
            {
                var rm = new RemoveBuffPacket(client.Partner.GeneralHandler, buffToRemove.BuffId).Serialize();

                // Sempre enviar para o próprio cliente
                client.Send(new RemoveBuffPacket(client.Partner.GeneralHandler, buffToRemove.BuffId));

                // E só depois para outros jogadores
                if (client.DungeonMap)
                    _dungeonServer.BroadcastForTargetTamers(client.TamerId, rm);
                else
                    _mapServer.BroadcastForTargetTamers(client.TamerId, rm);
            }

            client.Tamer.RemovePartnerPassiveBuff();
            await _sender.Send(new UpdateDigimonBuffListCommand(client.Partner.BuffList));

            // ---------------------------------------

            DigimonEvolutionEffectEnum evoEffect;

            if (evoStage == 8)
            {
                evoEffect = DigimonEvolutionEffectEnum.Back;
                client.Tamer.ActiveEvolution.SetDs(0);
                client.Tamer.ActiveEvolution.SetXg(0);
            }
            else
            {
                var evolutionType = _assets.DigimonBaseInfo.First(x => x.Type == targetStage.Type).EvolutionType;

                switch ((EvolutionRankEnum)evolutionType)
                {
                    case EvolutionRankEnum.Rookie:
                        evoEffect = DigimonEvolutionEffectEnum.Default;
                        client.Tamer.ActiveEvolution.SetDs(0);
                        client.Tamer.ActiveEvolution.SetXg(0);
                        break;

                    case EvolutionRankEnum.Champion:
                        evoEffect = DigimonEvolutionEffectEnum.Default;
                        if (client.Partner.Level < targetInfo.UnlockLevel || !client.Tamer.ConsumeDs(20))
                        {
                            client.Send(new DigimonEvolutionFailPacket());
                            return;
                        }
                        client.Tamer.ActiveEvolution.SetDs(8);
                        client.Tamer.ActiveEvolution.SetXg(0);
                        break;

                    case EvolutionRankEnum.Ultimate:
                        evoEffect = DigimonEvolutionEffectEnum.Default;
                        if (client.Partner.Level < targetInfo.UnlockLevel || !client.Tamer.ConsumeDs(50))
                        {
                            client.Send(new DigimonEvolutionFailPacket());
                            return;
                        }
                        client.Tamer.ActiveEvolution.SetDs(10);
                        client.Tamer.ActiveEvolution.SetXg(0);
                        break;

                    case EvolutionRankEnum.Mega:
                        evoEffect = DigimonEvolutionEffectEnum.Default;
                        if (client.Partner.Level < targetInfo.UnlockLevel || !client.Tamer.ConsumeDs(152))
                        {
                            client.Send(new DigimonEvolutionFailPacket());
                            return;
                        }
                        client.Tamer.ActiveEvolution.SetDs(12);
                        client.Tamer.ActiveEvolution.SetXg(0);
                        break;

                    case EvolutionRankEnum.BurstMode:
                        evoEffect = DigimonEvolutionEffectEnum.BurstMode;
                        if (targetInfo.RequiredItem > 0)
                        {
                            var itemToConsume = client.Tamer.Inventory.FindItemById(41002)
                                                ?? client.Tamer.Inventory.FindItemById(9400);

                            if (itemToConsume == null ||
                                (client.Partner.Level < targetInfo.UnlockLevel && !client.Tamer.ConsumeDs(148)) ||
                                itemToConsume.Amount < targetInfo.RequiredAmount)
                            {
                                client.Send(new DigimonEvolutionFailPacket());
                                return;
                            }

                            client.Tamer.Inventory.RemoveOrReduceItem(itemToConsume, targetInfo.RequiredAmount);
                            _logger.Verbose("{Amount} {Item} consumido para BurstMode.", targetInfo.RequiredAmount, itemToConsume.ItemInfo?.Name);
                        }
                        else
                        {
                            if (client.Partner.Level < targetInfo.UnlockLevel && !client.Tamer.ConsumeDs(148))
                            {
                                client.Send(new DigimonEvolutionFailPacket());
                                return;
                            }
                        }

                        client.Tamer.ActiveEvolution.SetDs(40);
                        client.Tamer.ActiveEvolution.SetXg(0);
                        client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));
                        break;

                    case EvolutionRankEnum.Jogress:
                        evoEffect = DigimonEvolutionEffectEnum.Default;
                        if (targetInfo.RequiredItem > 0)
                        {
                            var itemToConsume = client.Tamer.Inventory.FindItemBySection(targetInfo.RequiredItem);
                            if (itemToConsume == null ||
                                (client.Partner.Level < targetInfo.UnlockLevel && !client.Tamer.ConsumeDs(180)) ||
                                !client.Tamer.Inventory.RemoveOrReduceItem(itemToConsume, targetInfo.RequiredAmount))
                            {
                                client.Send(new DigimonEvolutionFailPacket());
                                return;
                            }
                        }
                        else
                        {
                            if (client.Partner.Level < targetInfo.UnlockLevel && !client.Tamer.ConsumeDs(180))
                            {
                                client.Send(new DigimonEvolutionFailPacket());
                                return;
                            }
                        }
                        client.Tamer.ActiveEvolution.SetDs(80);
                        client.Tamer.ActiveEvolution.SetXg(0);
                        break;

                    case EvolutionRankEnum.RookieX:
                        evoEffect = DigimonEvolutionEffectEnum.Default;
                        if (client.Partner.Level < targetInfo.UnlockLevel) { client.Send(new DigimonEvolutionFailPacket()); return; }
                        client.Tamer.ConsumeXg(68);
                        client.Tamer.ActiveEvolution.SetXg(2);
                        break;

                    case EvolutionRankEnum.ChampionX:
                        evoEffect = DigimonEvolutionEffectEnum.Default;
                        if (client.Partner.Level < targetInfo.UnlockLevel) { client.Send(new DigimonEvolutionFailPacket()); return; }
                        client.Tamer.ConsumeXg(92);
                        client.Tamer.ActiveEvolution.SetXg(4);
                        break;

                    case EvolutionRankEnum.UltimateX:
                        evoEffect = DigimonEvolutionEffectEnum.Default;
                        if (client.Partner.Level < targetInfo.UnlockLevel) { client.Send(new DigimonEvolutionFailPacket()); return; }
                        client.Tamer.ConsumeXg(130);
                        client.Tamer.ActiveEvolution.SetXg(6);
                        break;

                    case EvolutionRankEnum.MegaX:
                        evoEffect = DigimonEvolutionEffectEnum.Default;
                        if (client.Partner.Level < targetInfo.UnlockLevel) { client.Send(new DigimonEvolutionFailPacket()); return; }
                        client.Tamer.ConsumeXg(174);
                        client.Tamer.ActiveEvolution.SetXg(8);
                        break;

                    case EvolutionRankEnum.Capsule:
                        evoEffect = DigimonEvolutionEffectEnum.Unknown;
                        if (client.Partner.Level < targetInfo.UnlockLevel || !client.Tamer.ConsumeDs(75))
                        {
                            client.Send(new DigimonEvolutionFailPacket());
                            return;
                        }
                        client.Tamer.ActiveEvolution.SetDs(3);
                        client.Tamer.ActiveEvolution.SetXg(0);
                        break;

                    case EvolutionRankEnum.JogressX:
                    case EvolutionRankEnum.BurstModeX:
                        evoEffect = DigimonEvolutionEffectEnum.BurstMode;
                        if (client.Partner.Level < targetInfo.UnlockLevel) { client.Send(new DigimonEvolutionFailPacket()); return; }
                        client.Tamer.ConsumeXg(280);
                        client.Tamer.ActiveEvolution.SetXg(10);
                        break;

                    case EvolutionRankEnum.Extra:
                        evoEffect = DigimonEvolutionEffectEnum.Default;
                        if (client.Partner.Level < targetInfo.UnlockLevel) { client.Send(new DigimonEvolutionFailPacket()); return; }
                        client.Tamer.ActiveEvolution.SetDs(20);
                        client.Tamer.ActiveEvolution.SetXg(0);
                        break;

                    default:
                        _logger.Error("EvolutionRankEnum não registrado: {Rank}", (EvolutionRankEnum)evolutionType);
                        client.Send(new DigimonEvolutionFailPacket());
                        return;
                }

                if (client.Tamer.HasXai)
                {
                    client.Send(new XaiInfoPacket(client.Tamer.Xai));
                    client.Send(new TamerXaiResourcesPacket(client.Tamer.XGauge, client.Tamer.XCrystals));
                }
            }

            // logs
            if (evoStage == 8)
                _logger.Verbose("Tamer {Name} devolveu ({Id}:{Nm}) de {From} para {To}.",
                    client.Tamer.Name, client.Partner.Id, client.Partner.Name, client.Partner.CurrentType, targetStage.Type);
            else
                _logger.Verbose("Tamer {Name} evoluiu ({Id}:{Nm}) de {From} para {To}.",
                    client.Tamer.Name, client.Partner.Id, client.Partner.Name, client.Partner.CurrentType, targetStage.Type);

            // aplica tipo alvo
            client.Partner.UpdateCurrentType(targetStage.Type);

            // parar ride (client + outros)
            if (client.Tamer.Riding)
            {
                client.Tamer.StopRideMode();
                client.Send(new UpdateMovementSpeedPacket(client.Tamer));
                client.Send(new RideModeStopPacket(client.Tamer.GeneralHandler, client.Partner.GeneralHandler));

                var stopRidePkt = UtilitiesFunctions.GroupPackets(
                    new UpdateMovementSpeedPacket(client.Tamer).Serialize(),
                    new RideModeStopPacket(client.Tamer.GeneralHandler, client.Partner.GeneralHandler).Serialize()
                );

                if (client.DungeonMap)
                    _dungeonServer.BroadcastForTargetTamers(client.TamerId, stopRidePkt);
                else
                    _mapServer.BroadcastForTargetTamers(client.TamerId, stopRidePkt);
            }

            // >>>> FIX PRINCIPAL: envia SEMPRE ao cliente e só depois aos outros
            var evoPkt = new DigimonEvolutionSucessPacket(
                client.Tamer.GeneralHandler,
                client.Partner.GeneralHandler,
                client.Partner.CurrentType,
                evoEffect // <-- usa o efeito calculado acima!
            ).Serialize();

            // envia direto ao próprio cliente
            client.Send(new DigimonEvolutionSucessPacket(
                client.Tamer.GeneralHandler,
                client.Partner.GeneralHandler,
                client.Partner.CurrentType,
                evoEffect));

            // e agora apenas para os outros jogadores que o veem
            if (client.DungeonMap)
                _dungeonServer.BroadcastForTargetTamers(client.TamerId, evoPkt);
            else
                _mapServer.BroadcastForTargetTamers(client.TamerId, evoPkt);

            // ------------------------------------------------------------

            UpdateSkillCooldown(client);

            var currentHp = client.Partner.CurrentHp;
            var currentMaxHp = client.Partner.HP;
            var currentDs = client.Partner.CurrentDs;
            var currentMaxDs = client.Partner.DS;

            client.Tamer.Partner.SetBaseInfo(_statusManager.GetDigimonBaseInfo(client.Tamer.Partner.CurrentType));
            client.Tamer.Partner.SetBaseStatus(_statusManager.GetDigimonBaseStatus(client.Tamer.Partner.CurrentType, client.Tamer.Partner.Level, client.Tamer.Partner.Size));

            client.Partner.SetSealStatus(_assets.SealInfo);
            client.Tamer.SetPartnerPassiveBuff();

            if (evoStage != 8)
                client.Partner.FullHeal();
            else
                client.Partner.AdjustHpAndDs(currentHp, currentMaxHp, currentDs, currentMaxDs);

            var currentTitleBuff = _assets.AchievementAssets.FirstOrDefault(x => x.QuestId == client.Tamer.CurrentTitle && x.BuffId > 0);

            if (currentTitleBuff != null)
            {
                foreach (var buff in client.Tamer.Partner.BuffList.ActiveBuffs.Where(x => x.BuffId != currentTitleBuff.BuffId))
                    buff.SetBuffInfo(_assets.BuffInfo.FirstOrDefault(x =>
                        (x.SkillCode == buff.SkillId && buff.BuffInfo == null) ||
                        (x.DigimonSkillCode == buff.SkillId && buff.BuffInfo == null)));

                if (client.Tamer.Partner.BuffList.TamerBaseSkill() != null)
                {
                    var buffToApply = client.Tamer.Partner.BuffList.Buffs
                        .Where(x => x.Duration == 0 && x.BuffId != currentTitleBuff.BuffId)
                        .ToList();

                    buffToApply.ForEach(b =>
                    {
                        var add = new AddBuffPacket(client.Tamer.Partner.GeneralHandler, b.BuffId, b.SkillId, (short)b.TypeN, 0).Serialize();
                        if (client.DungeonMap)
                            _dungeonServer.BroadcastForTamerViewsAndSelf(client.Tamer.Id, add);
                        else
                            _mapServer.BroadcastForTamerViewsAndSelf(client.Tamer.Id, add);
                    });
                }
            }
            else
            {
                foreach (var buff in client.Tamer.Partner.BuffList.ActiveBuffs)
                    buff.SetBuffInfo(_assets.BuffInfo.FirstOrDefault(x =>
                        (x.SkillCode == buff.SkillId && buff.BuffInfo == null) ||
                        (x.DigimonSkillCode == buff.SkillId && buff.BuffInfo == null)));

                if (client.Tamer.Partner.BuffList.TamerBaseSkill() != null)
                {
                    var buffToApply = client.Tamer.Partner.BuffList.Buffs.Where(x => x.Duration == 0).ToList();
                    buffToApply.ForEach(b =>
                    {
                        var add = new AddBuffPacket(client.Tamer.Partner.GeneralHandler, b.BuffId, b.SkillId, (short)b.TypeN, 0).Serialize();
                        if (client.DungeonMap)
                            _dungeonServer.BroadcastForTamerViewsAndSelf(client.Tamer.Id, add);
                        else
                            _mapServer.BroadcastForTamerViewsAndSelf(client.Tamer.Id, add);
                    });
                }
            }

            client.Send(new UpdateStatusPacket(client.Tamer));
            client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));

            // -- PARTY -------------------------------------------
            var party = _partyManager.FindParty(client.TamerId);
            if (party != null)
            {
                party.UpdateMember(party[client.TamerId], client.Tamer);

                foreach (var target in party.Members.Values)
                {
                    var targetClient = _mapServer.FindClientByTamerId(target.Id) ?? _dungeonServer.FindClientByTamerId(target.Id);
                    if (targetClient == null || target.Id == client.Tamer.Id) continue;
                    targetClient.Send(new PartyMemberInfoPacket(party[client.TamerId]));
                }
            }

            await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
            await _sender.Send(new UpdatePartnerCurrentTypeCommand(client.Partner));
            await _sender.Send(new UpdateCharacterActiveEvolutionCommand(client.Tamer.ActiveEvolution));
            await _sender.Send(new UpdateCharacterBasicInfoCommand(client.Tamer));
            await _sender.Send(new UpdateDigimonBuffListCommand(client.Partner.BuffList));
        }

        private void UpdateSkillCooldown(GameClient client)
        {
            if (!client.Tamer.Partner.HasActiveSkills())
                return;

            foreach (var evolution in client.Tamer.Partner.Evolutions)
            {
                foreach (var skill in evolution.Skills)
                {
                    if (skill.Duration > 0 && skill.Expired)
                        skill.ResetCooldown();
                }

                _sender.Send(new UpdateEvolutionCommand(evolution));
            }

            List<int> skillIds = new List<int>(5);
            var packetEvolution = client.Tamer.Partner.Evolutions.FirstOrDefault(x => x.Type == client.Tamer.Partner.CurrentType);

            if (packetEvolution != null)
            {
                var slot = -1;
                foreach (var item in packetEvolution.Skills)
                {
                    slot++;
                    var skillInfo = _assets.DigimonSkillInfo.FirstOrDefault(x => x.Type == client.Partner.CurrentType && x.Slot == slot);
                    if (skillInfo != null)
                        skillIds.Add(skillInfo.SkillId);
                }

                client?.Send(new SkillUpdateCooldownPacket(client.Tamer.Partner.GeneralHandler, client.Tamer.Partner.CurrentType, packetEvolution, skillIds));
            }
        }
    }
}
