using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models;
using DigitalWorldOnline.Commons.Models.Character;
using DigitalWorldOnline.Commons.Models.Config;
using DigitalWorldOnline.Commons.Models.Digimon;
using DigitalWorldOnline.Commons.Models.Summon;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer.Combat;
using DigitalWorldOnline.Commons.Utils;
using DigitalWorldOnline.GameHost;
using Serilog;
using System;
using System.Linq;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class PartnerAttackPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.PartnerAttack;

        private readonly MapServer _mapServer;
        private readonly PvpServer _pvpServer;
        private readonly DungeonsServer _dungeonServer;
        private readonly ILogger _logger;

        public PartnerAttackPacketProcessor(
            MapServer mapServer,
            PvpServer pvpServer,
            ILogger logger,
            DungeonsServer dungeonsServer)
        {
            _mapServer = mapServer;
            _pvpServer = pvpServer;
            _dungeonServer = dungeonsServer;
            _logger = logger;
        }

        public Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);
            var attackerHandler = packet.ReadInt();
            var targetHandler = packet.ReadInt();

            try
            {
                if (client?.Tamer == null || client.Partner == null)
                    return Task.CompletedTask;

                // Normalizamos relógio (Utc) para reduzir chance de desync por fuso/clock skew
                var now = DateTime.UtcNow;

                // Helper p/ decidir o "ambiente" do broadcast/consulta
                var useDungeon = client.DungeonMap;

                // 1) PvP: parceiro alvo (de outro tamer)
                var targetPartner = _mapServer.GetEnemyByHandler(client.Tamer.Location.MapId, targetHandler, client.TamerId);

                if (!useDungeon && targetPartner != null)
                {
                    // PVP desligado do alvo
                    if (!targetPartner.Character.PvpMap)
                    {
                        ForceStopCombatForSelf(client, attackerHandler);
                        client.Send(new SystemMessagePacket($"Jogador {targetPartner.Name} está com o PvP desativado."));
                        return Task.CompletedTask;
                    }

                    if (client.Tamer.PvpMap && targetPartner.Character.PvpMap)
                    {
                        return HandleVsPlayer(client, attackerHandler, targetHandler, targetPartner, now);
                    }
                }

                // 2) DUNGEON: Summon -> Mob comum (por id de handler com flag summon)
                if (useDungeon)
                {
                    // Prioridade: Summon do dungeon
                    var targetSummon = _dungeonServer.GetMobByHandler(client.Tamer.Location.MapId, targetHandler, true, client.TamerId);
                    if (targetSummon != null)
                        return HandleVsSummonDungeon(client, attackerHandler, targetHandler, targetSummon, now);

                    // Mob "normal" do dungeon
                    var targetMobD = _dungeonServer.GetMobByHandler(client.Tamer.Location.MapId, targetHandler, client.TamerId);
                    if (targetMobD != null)
                        return HandleVsMobDungeon(client, attackerHandler, targetHandler, targetMobD, now);
                }
                else
                {
                    // 3) MAP NORMAL: Summon primeiro…
                    var targetSummonN = _mapServer.GetMobByHandler(client.Tamer.Location.MapId, targetHandler, true, client.TamerId);
                    if (targetSummonN != null)
                        return HandleVsSummonMap(client, attackerHandler, targetHandler, targetSummonN, now);

                    // …depois mob normal
                    var targetMob = _mapServer.GetMobByHandler(client.Tamer.Location.MapId, targetHandler, client.TamerId);
                    if (targetMob != null)
                        return HandleVsMobMap(client, attackerHandler, targetHandler, targetMob, now);
                }

                // Se chegou aqui, alvo não existe mais: limpe estado de combate se não houver mais agressor
                ForceStopCombatIfNoAggressors(client, attackerHandler, useDungeon);

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[{Tamer}] PartnerAttack exception.", client?.Tamer?.Name);
                client?.Disconnect();
                return Task.CompletedTask;
            }
        }

        // ======================================================================
        // ==============   FLUXOS ESPECÍFICOS (PvP / PvE / Dungeon)   ==========
        // ======================================================================

        private Task HandleVsPlayer(GameClient client, int attackerHandler, int targetHandler, DigimonModel targetPartner, DateTime nowUtc)
        {
            // cooldown: se ainda em AS, não processa hit; mantém auto-attack
            if (nowUtc < client.Partner.LastHitTime.AddMilliseconds(client.Partner.AS))
            {
                client.Partner.StartAutoAttack();
                EnsureTarget(client, targetPartner);
                return Task.CompletedTask;
            }

            EnsureCombatOn(client, attackerHandler, isDungeon: false);
            EnsureTarget(client, targetPartner);

            var missed = false; // sua lógica de miss PVP pode entrar aqui se houver
            if (missed)
            {
                SendMiss(client, attackerHandler, targetHandler, isDungeon: false);
                return Task.CompletedTask;
            }

            // Dano
            var crit = 0.0;
            var blocked = false;
            var final = CalculateFinalDamage(client, targetPartner, out crit, out blocked);
            if (final <= 0) final = 1;

            var newHp = targetPartner.ReceiveDamage(final);
            var hitType = blocked ? 2 : (crit > 0 ? 1 : 0);

            if (newHp > 0)
            {
                SendHit(client, attackerHandler, targetHandler, final, targetPartner.HP, newHp, hitType, isDungeon: false);
            }
            else
            {
                SendKill(client, attackerHandler, targetHandler, final, hitType, isDungeon: false);
                targetPartner.Character.Die();

                // encerra combate se ninguém mais te agride
                if (!_mapServer.EnemiesAttacking(client.Tamer.Location.MapId, client.Partner.Id, client.TamerId))
                    ForceStopCombatForSelf(client, attackerHandler);
            }

            AfterHitBookkeeping(client, nowUtc);
            return Task.CompletedTask;
        }

        private Task HandleVsSummonDungeon(GameClient client, int attackerHandler, int targetHandler, SummonMobModel target, DateTime nowUtc)
        {
            if (CommonDungeonPreChecks(client, attackerHandler, targetHandler, target, nowUtc))
                return Task.CompletedTask;

            var missed = !client.Tamer.GodMode && client.Tamer.CanMissHit(true);
            if (missed)
            {
                SendMiss(client, attackerHandler, targetHandler, isDungeon: true);
                AfterHitBookkeeping(client, nowUtc);
                return Task.CompletedTask;
            }

            var crit = 0.0;
            var blocked = false;
            var final = client.Tamer.GodMode ? target.CurrentHP : CalculateFinalDamage(client, target, out crit, out blocked);
            final = DebuffReductionDamage(client, final);
            if (final <= 0) final = 1;
            if (final > target.CurrentHP) final = target.CurrentHP;

            var newHp = target.ReceiveDamage(final, client.TamerId);
            var hitType = blocked ? 2 : (crit > 0 ? 1 : 0);

            if (newHp > 0)
            {
                SendHit(client, attackerHandler, targetHandler, final, target.HPValue, newHp, hitType, isDungeon: true);
            }
            else
            {
                client.Partner.SetEndAttacking(client.Partner.AS * -2);
                SendKill(client, attackerHandler, targetHandler, final, hitType, isDungeon: true);
                target?.Die();

                if (!_dungeonServer.MobsAttacking(client.Tamer.Location.MapId, client.TamerId))
                    ForceStopCombatForSelf(client, attackerHandler, true);
            }

            AfterHitBookkeeping(client, nowUtc);
            return Task.CompletedTask;
        }

        private Task HandleVsMobDungeon(GameClient client, int attackerHandler, int targetHandler, MobConfigModel target, DateTime nowUtc)
        {
            if (CommonDungeonPreChecks(client, attackerHandler, targetHandler, target, nowUtc))
                return Task.CompletedTask;

            var missed = !client.Tamer.GodMode && client.Tamer.CanMissHit();
            if (missed)
            {
                SendMiss(client, attackerHandler, targetHandler, isDungeon: true);
                AfterHitBookkeeping(client, nowUtc);
                return Task.CompletedTask;
            }

            var crit = 0.0;
            var blocked = false;
            var final = client.Tamer.GodMode ? target.CurrentHP : CalculateFinalDamage(client, target, out crit, out blocked);
            final = DebuffReductionDamage(client, final);
            if (final <= 0) final = 1;
            if (final > target.CurrentHP) final = target.CurrentHP;

            var newHp = target.ReceiveDamage(final, client.TamerId);
            var hitType = blocked ? 2 : (crit > 0 ? 1 : 0);

            if (newHp > 0)
            {
                SendHit(client, attackerHandler, targetHandler, final, target.HPValue, newHp, hitType, isDungeon: true);
            }
            else
            {
                client.Partner.SetEndAttacking(client.Partner.AS * -2);
                SendKill(client, attackerHandler, targetHandler, final, hitType, isDungeon: true);
                target?.Die();

                if (!_dungeonServer.MobsAttacking(client.Tamer.Location.MapId, client.TamerId))
                    ForceStopCombatForSelf(client, attackerHandler, true);
            }

            AfterHitBookkeeping(client, nowUtc);
            return Task.CompletedTask;
        }

        private Task HandleVsSummonMap(GameClient client, int attackerHandler, int targetHandler, SummonMobModel target, DateTime nowUtc)
        {
            if (CommonMapPreChecks(client, attackerHandler, targetHandler, target, nowUtc))
                return Task.CompletedTask;

            var missed = !client.Tamer.GodMode && client.Tamer.CanMissHit(true);
            if (missed)
            {
                SendMiss(client, attackerHandler, targetHandler, isDungeon: false);
                AfterHitBookkeeping(client, nowUtc);
                return Task.CompletedTask;
            }

            var crit = 0.0;
            var blocked = false;
            var final = client.Tamer.GodMode ? target.CurrentHP : CalculateFinalDamage(client, target, out crit, out blocked);
            final = DebuffReductionDamage(client, final);
            if (final <= 0) final = 1;
            if (final > target.CurrentHP) final = target.CurrentHP;

            var newHp = target.ReceiveDamage(final, client.TamerId);
            var hitType = blocked ? 2 : (crit > 0 ? 1 : 0);

            if (newHp > 0)
            {
                SendHit(client, attackerHandler, targetHandler, final, target.HPValue, newHp, hitType, isDungeon: false);
            }
            else
            {
                client.Partner.SetEndAttacking(client.Partner.AS * -2);
                SendKill(client, attackerHandler, targetHandler, final, hitType, isDungeon: false);
                target?.Die();

                if (!_mapServer.MobsAttacking(client.Tamer.Location.MapId, client.TamerId, true))
                    ForceStopCombatForSelf(client, attackerHandler);
            }

            AfterHitBookkeeping(client, nowUtc);
            return Task.CompletedTask;
        }

        private Task HandleVsMobMap(GameClient client, int attackerHandler, int targetHandler, MobConfigModel target, DateTime nowUtc)
        {
            if (CommonMapPreChecks(client, attackerHandler, targetHandler, target, nowUtc))
                return Task.CompletedTask;

            var missed = !client.Tamer.GodMode && client.Tamer.CanMissHit();
            if (missed)
            {
                SendMiss(client, attackerHandler, targetHandler, isDungeon: false);
                AfterHitBookkeeping(client, nowUtc);
                return Task.CompletedTask;
            }

            var crit = 0.0;
            var blocked = false;
            var final = client.Tamer.GodMode ? target.CurrentHP : CalculateFinalDamage(client, target, out crit, out blocked);
            final = DebuffReductionDamage(client, final);
            if (final <= 0) final = 1;
            if (final > target.CurrentHP) final = target.CurrentHP;

            var newHp = target.ReceiveDamage(final, client.TamerId);
            var hitType = blocked ? 2 : (crit > 0 ? 1 : 0);

            if (newHp > 0)
            {
                SendHit(client, attackerHandler, targetHandler, final, target.HPValue, newHp, hitType, isDungeon: false);
            }
            else
            {
                client.Partner.SetEndAttacking(client.Partner.AS * -2);
                SendKill(client, attackerHandler, targetHandler, final, hitType, isDungeon: false);
                target?.Die();

                if (!_mapServer.MobsAttacking(client.Tamer.Location.MapId, client.TamerId))
                    ForceStopCombatForSelf(client, attackerHandler);
            }

            AfterHitBookkeeping(client, nowUtc);
            return Task.CompletedTask;
        }

        // ======================================================================
        // ============================== HELPERS ================================
        // ======================================================================

        private bool CommonDungeonPreChecks(GameClient client, int attackerHandler, int targetHandler, dynamic target, DateTime nowUtc)
        {
            // AS cooldown
            if (nowUtc < client.Partner.LastHitTime.AddMilliseconds(client.Partner.AS))
            {
                client.Partner.StartAutoAttack();
                EnsureTarget(client, target);
                return true;
            }

            client.Partner.SetEndAttacking(0);

            EnsureCombatOn(client, attackerHandler, isDungeon: true);

            if (!target.InBattle)
            {
                _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, new SetCombatOnPacket(targetHandler).Serialize());
                target.StartBattle(client.Tamer);
            }
            else
            {
                target.AddTarget(client.Tamer);
            }

            EnsureTarget(client, target);
            client.Tamer.Partner.StartAutoAttack();
            return false;
        }

        private bool CommonMapPreChecks(GameClient client, int attackerHandler, int targetHandler, dynamic target, DateTime nowUtc)
        {
            if (nowUtc < client.Partner.LastHitTime.AddMilliseconds(client.Partner.AS))
            {
                client.Partner.StartAutoAttack();
                EnsureTarget(client, target);
                return true;
            }

            client.Partner.SetEndAttacking(0);

            EnsureCombatOn(client, attackerHandler, isDungeon: false);

            if (!target.InBattle)
            {
                _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new SetCombatOnPacket(targetHandler).Serialize());
                target.StartBattle(client.Tamer);
            }
            else
            {
                target.AddTarget(client.Tamer);
            }

            EnsureTarget(client, target);
            client.Tamer.Partner.StartAutoAttack();
            return false;
        }

        private void EnsureTarget(GameClient client, dynamic target)
        {
            // se já está atacando outro handler, muda o alvo e mantém auto-attack
            client.Tamer.SetHidden(false);
            client.Tamer.UpdateTarget(target);
        }

        // SUBSTITUA TODO o método atual por este:
        private void EnsureCombatOn(GameClient client, int attackerHandler, bool isDungeon)
        {
            if (!client.Tamer.InBattle)
            {
                client.Tamer.SetHidden(false);

                // envia primeiro para o próprio cliente (evita desync)
                client.Send(new SetCombatOnPacket(attackerHandler).Serialize());

                // depois para quem o vê (mapa ou dungeon)
                SendToSelfThenViews(client, new SetCombatOnPacket(attackerHandler).Serialize(), isDungeon);

                // escolha explícita do alvo para casar com o overload certo de StartBattle(...)
                if (client.Tamer.TargetMob != null)
                {
                    client.Tamer.StartBattle(client.Tamer.TargetMob);               // MobConfigModel
                }
                else if (client.Tamer.TargetSummonMob != null)
                {
                    client.Tamer.StartBattle(client.Tamer.TargetSummonMob);         // SummonMobModel
                }
                else if (client.Tamer.TargetPartner != null)
                {
                    client.Tamer.StartBattle(client.Tamer.TargetPartner);           // DigimonModel
                }
            }
            else
            {
                // Já em combate: garante feedback local imediato
                client.Send(new SetCombatOnPacket(attackerHandler).Serialize());
            }
        }

        private void ForceStopCombatIfNoAggressors(GameClient client, int attackerHandler, bool isDungeon)
        {
            var attacking =
                isDungeon
                    ? _dungeonServer.MobsAttacking(client.Tamer.Location.MapId, client.TamerId)
                    : _mapServer.MobsAttacking(client.Tamer.Location.MapId, client.TamerId);

            if (!attacking)
                ForceStopCombatForSelf(client, attackerHandler, isDungeon);
        }

        // ADICIONE este helper em qualquer lugar da classe:
        private static void ClearTargets(CharacterModel tamer)
        {
            if (tamer.TargetMob != null)
                tamer.UpdateTarget((MobConfigModel)null);

            if (tamer.TargetSummonMob != null)
                tamer.UpdateTarget((SummonMobModel)null);

            if (tamer.TargetPartner != null)
                tamer.UpdateTarget((DigimonModel)null);
        }

        // SUBSTITUA o método atual por este:
        private void ForceStopCombatForSelf(GameClient client, int attackerHandler, bool isDungeon = false)
        {
            client.Tamer.StopBattle(isDungeon);

            // evita ambiguidade de null escolhendo o overload certo
            ClearTargets(client.Tamer);

            SendToSelfThenViews(client, new SetCombatOffPacket(attackerHandler).Serialize(), isDungeon);
        }

        private void SendMiss(GameClient c, int attacker, int target, bool isDungeon)
        {
            SendToSelfThenViews(c, new MissHitPacket(attacker, target).Serialize(), isDungeon);
        }

        private void SendHit(GameClient c, int attacker, int target, int dmg, int hpMax, int hpNew, int hitType, bool isDungeon)
        {
            var pkt = new HitPacket(attacker, target, dmg, hpMax, hpNew, hitType).Serialize();
            SendToSelfThenViews(c, pkt, isDungeon);
        }

        private void SendKill(GameClient c, int attacker, int target, int dmg, int hitType, bool isDungeon)
        {
            var pkt = new KillOnHitPacket(attacker, target, dmg, hitType).Serialize();
            SendToSelfThenViews(c, pkt, isDungeon);
        }

        private void SendToSelfThenViews(GameClient c, byte[] payload, bool isDungeon)
        {
            c.Send(payload); // sempre para o próprio cliente primeiro (evita “lag visual”/desync)
            if (isDungeon)
                _dungeonServer.BroadcastForTamerViewsAndSelf(c.TamerId, payload);
            else
                _mapServer.BroadcastForTamerViewsAndSelf(c.TamerId, payload);
        }

        // SUBSTITUA o corpo do método (ou apenas a linha problemática) por:
        private void AfterHitBookkeeping(GameClient client, DateTime nowUtc)
        {
            client.Tamer.Partner.UpdateLastHitTime(); // <- sem argumentos
            client.Partner.StartAutoAttack();
        }

        // ======================================================================
        // ========================== CÁLCULO DE DANO ============================
        // ======================================================================

        private static int DebuffReductionDamage(GameClient client, int finalDmg)
        {
            if (finalDmg <= 0) return 0;

            if (client.Tamer.Partner.DebuffList.ActiveDebuffReductionDamage())
            {
                var debuffInfo = client.Tamer.Partner.DebuffList.ActiveBuffs
                    .Where(buff => buff.BuffInfo?.SkillInfo?.Apply
                        .Any(apply => apply.Attribute == Commons.Enums.SkillCodeApplyAttributeEnum.AttackPowerDown) == true)
                    .ToList();

                var somaPercent = 0.0;
                var flat = 0;

                foreach (var debuff in debuffInfo)
                {
                    foreach (var apply in debuff.BuffInfo!.SkillInfo!.Apply)
                    {
                        switch (apply.Type)
                        {
                            case SkillCodeApplyTypeEnum.Default:
                                flat += apply.Value;
                                break;

                            case SkillCodeApplyTypeEnum.AlsoPercent:
                            case SkillCodeApplyTypeEnum.Percent:
                                somaPercent += (apply.Value + debuff.TypeN * apply.IncreaseValue) / 100.0;
                                break;

                            case SkillCodeApplyTypeEnum.Unknown200:
                                somaPercent += (apply.AdditionalValue) / 100.0;
                                break;
                        }
                    }
                }

                finalDmg = Math.Max(0, (int)Math.Round((finalDmg - flat) * (1.0 - somaPercent)));
            }

            return finalDmg;
        }

        // Mob
        private static int CalculateFinalDamage(GameClient client, MobConfigModel targetMob, out double critBonusMultiplier, out bool blocked)
        {
            int baseDamage = client.Tamer.Partner.AT - targetMob.DEValue;
            if (baseDamage < client.Tamer.Partner.AT * 0.5)
                baseDamage = (int)(client.Tamer.Partner.AT * 0.6);

            critBonusMultiplier = 0.0;
            var critChance = client.Tamer.Partner.CC / 100.0;
            blocked = false;

            if (critChance >= UtilitiesFunctions.RandomDouble())
            {
                var critDamageMultiplier = client.Tamer.Partner.CD / 100.0;
                critBonusMultiplier = baseDamage * (critDamageMultiplier / 100.0);
            }

            blocked = targetMob.BLValue >= UtilitiesFunctions.RandomDouble();

            var attributeMultiplier = 0.0;
            if (client.Tamer.Partner.BaseInfo.Attribute.HasAttributeAdvantage(targetMob.Attribute))
            {
                var attExp = client.Tamer.Partner.GetAttributeExperience();
                var attValuePercent = (client.Partner.ATT / 100.0) / 100.0;
                attributeMultiplier = ((0.5 + attValuePercent) * attExp) / 10000.0;
            }
            else if (targetMob.Attribute.HasAttributeAdvantage(client.Tamer.Partner.BaseInfo.Attribute))
            {
                attributeMultiplier -= 0.25;
            }

            var elementMultiplier = 0.0;
            if (client.Tamer.Partner.BaseInfo.Element.HasElementAdvantage(targetMob.Element))
            {
                var vlrAtual = client.Tamer.Partner.GetElementExperience();
                elementMultiplier = (0.5 * vlrAtual) / 10000.0;
            }
            else if (targetMob.Element.HasElementAdvantage(client.Tamer.Partner.BaseInfo.Element))
            {
                elementMultiplier -= 0.25;
            }

            if (blocked) baseDamage /= 2;

            var finalDamage = (int)Math.Max(1, Math.Floor(baseDamage + critBonusMultiplier +
                (baseDamage * attributeMultiplier) + (baseDamage * elementMultiplier)));

            return finalDamage;
        }

        // Summon
        private static int CalculateFinalDamage(GameClient client, SummonMobModel targetSummonMob, out double critBonusMultiplier, out bool blocked)
        {
            int baseDamage = client.Tamer.Partner.AT - targetSummonMob.DEValue;
            if (baseDamage < client.Tamer.Partner.AT * 0.5)
                baseDamage = (int)(client.Tamer.Partner.AT * 0.6);

            critBonusMultiplier = 0.0;
            var critChance = client.Tamer.Partner.CC / 100.0;

            if (critChance >= UtilitiesFunctions.RandomDouble())
            {
                var critDamageMultiplier = client.Tamer.Partner.CD / 100.0;
                critBonusMultiplier = baseDamage * (critDamageMultiplier / 100.0);
            }

            blocked = targetSummonMob.BLValue >= UtilitiesFunctions.RandomDouble();

            var attributeMultiplier = 0.0;
            if (client.Tamer.Partner.BaseInfo.Attribute.HasAttributeAdvantage(targetSummonMob.Attribute))
            {
                var attExp = client.Tamer.Partner.GetAttributeExperience();
                var attValuePercent = (client.Partner.ATT / 100.0) / 100.0;
                attributeMultiplier = ((0.5 + attValuePercent) * attExp) / 10000.0;
            }
            else if (targetSummonMob.Attribute.HasAttributeAdvantage(client.Tamer.Partner.BaseInfo.Attribute))
            {
                attributeMultiplier -= 0.25;
            }

            var elementMultiplier = 0.0;
            if (client.Tamer.Partner.BaseInfo.Element.HasElementAdvantage(targetSummonMob.Element))
            {
                var vlrAtual = client.Tamer.Partner.GetElementExperience();
                elementMultiplier = (0.5 * vlrAtual) / 10000.0;
            }
            else if (targetSummonMob.Element.HasElementAdvantage(client.Tamer.Partner.BaseInfo.Element))
            {
                elementMultiplier -= 0.25;
            }

            if (blocked) baseDamage /= 2;

            return (int)Math.Max(1, Math.Floor(baseDamage + critBonusMultiplier +
                (baseDamage * attributeMultiplier) + (baseDamage * elementMultiplier)));
        }

        // Player
        private static int CalculateFinalDamage(GameClient client, DigimonModel targetPartner, out double critBonusMultiplier, out bool blocked)
        {
            var baseDamage = (client.Tamer.Partner.AT / targetPartner.DE * 150) + UtilitiesFunctions.RandomInt(5, 50);
            if (baseDamage < 0) baseDamage = 0;

            critBonusMultiplier = 0.0;
            var critChance = client.Tamer.Partner.CC / 100.0;

            if (critChance >= UtilitiesFunctions.RandomDouble())
            {
                var vlrAtual = client.Tamer.Partner.CD;
                var bonusMax = 1.50;
                var expMax = 10000.0;
                critBonusMultiplier = (bonusMax * vlrAtual) / expMax;
            }

            blocked = targetPartner.BL >= UtilitiesFunctions.RandomDouble();

            var levelBonusMultiplier = client.Tamer.Partner.Level > targetPartner.Level
                ? (0.01f * (client.Tamer.Partner.Level - targetPartner.Level))
                : 0;

            var attributeMultiplier = 0.0;
            if (client.Tamer.Partner.BaseInfo.Attribute.HasAttributeAdvantage(targetPartner.BaseInfo.Attribute))
            {
                var vlrAtual = client.Tamer.Partner.GetAttributeExperience();
                attributeMultiplier = (1.00 * vlrAtual) / 10000.0;
            }
            else if (targetPartner.BaseInfo.Attribute.HasAttributeAdvantage(client.Tamer.Partner.BaseInfo.Attribute))
            {
                attributeMultiplier = -0.25;
            }

            var elementMultiplier = 0.0;
            if (client.Tamer.Partner.BaseInfo.Element.HasElementAdvantage(targetPartner.BaseInfo.Element))
            {
                var vlrAtual = client.Tamer.Partner.GetElementExperience();
                elementMultiplier = (0.50 * vlrAtual) / 10000.0;
            }
            else if (targetPartner.BaseInfo.Element.HasElementAdvantage(client.Tamer.Partner.BaseInfo.Element))
            {
                elementMultiplier = -0.50;
            }

            baseDamage /= blocked ? 2 : 1;

            return (int)Math.Floor(baseDamage +
                (baseDamage * critBonusMultiplier) +
                (baseDamage * levelBonusMultiplier) +
                (baseDamage * attributeMultiplier) +
                (baseDamage * elementMultiplier));
        }
    }
}
