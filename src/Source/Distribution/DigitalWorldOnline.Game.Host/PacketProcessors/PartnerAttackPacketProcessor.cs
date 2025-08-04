using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models;
using DigitalWorldOnline.Commons.Models.Config;
using DigitalWorldOnline.Commons.Models.Digimon;
using DigitalWorldOnline.Commons.Models.Summon;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer.Combat;
using DigitalWorldOnline.Commons.Utils;
using DigitalWorldOnline.GameHost;
using Serilog;
using System;

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
            ILogger logger, DungeonsServer dungeonsServer)
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

            client.Tamer.StopBattle();
            client.Partner.StopAutoAttack();

            try
            {
                var targetPartner = _mapServer.GetEnemyByHandler(client.Tamer.Location.MapId, targetHandler, client.TamerId);
                
                if (targetPartner != null)
                {
                    if (targetPartner.Character.PvpMap == false)
                    {
                        client.Tamer.StopBattle();
                        client.Partner.StopAutoAttack();
                        client.Send(new SetCombatOffPacket(client.Partner.GeneralHandler).Serialize());
                        client.Send(new SystemMessagePacket($"Jogador {targetPartner.Name} esta com o PVP Off!"));
                    }
                }

                if (client.Tamer.PvpMap && targetPartner.Character.PvpMap && targetPartner != null)
                {
                    client.Partner.StartAutoAttack();

                    if (targetPartner.Alive)
                    {
                        if (client.Partner.IsAttacking)
                        {
                            if (client.Tamer.TargetMob?.GeneralHandler != targetPartner.GeneralHandler)
                            {
                                _logger.Verbose($"Character {client.Tamer.Id} switched target to partner {targetPartner.Id} - {targetPartner.Name}.");
                                client.Tamer.SetHidden(false);
                                client.Tamer.UpdateTarget(targetPartner);
                                client.Partner.StartAutoAttack();
                            }
                        }
                        else
                        {
                            if (DateTime.Now < client.Partner.LastHitTime.AddMilliseconds(client.Partner.AS))
                            {
                                client.Partner.StartAutoAttack();
                                return Task.CompletedTask;
                            }


                            if (!client.Tamer.InBattle)
                            {
                                _logger.Verbose($"Character {client.Tamer.Id} engaged partner {targetPartner.Id} - {targetPartner.Name}.");
                                client.Tamer.SetHidden(false);

                                _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new SetCombatOnPacket(attackerHandler).Serialize());
                                client.Tamer.StartBattle(targetPartner);
                            }
                            else
                            {
                                _logger.Verbose($"Character {client.Tamer.Id} switched to partner {targetPartner.Id} - {targetPartner.Name}.");
                                client.Tamer.SetHidden(false);
                                client.Tamer.UpdateTarget(targetPartner);
                            }

                            if (!targetPartner.Character.InBattle)
                            {
                                _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new SetCombatOnPacket(targetHandler).Serialize());
                            }

                            targetPartner.Character.StartBattle(client.Partner);

                            client.Tamer.Partner.StartAutoAttack();

                            var missed = false;

                            if (missed)
                            {
                                _logger.Verbose($"Partner {client.Tamer.Partner.Id} missed hit on {client.Tamer.TargetPartner.Id} - {client.Tamer.TargetPartner.Name}.");
                                _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new MissHitPacket(attackerHandler, targetHandler).Serialize());
                            }
                            else
                            {
                                #region Hit Damage
                                var critBonusMultiplier = 0.00;
                                var blocked = false;
                                var finalDmg = CalculateFinalDamage(client, targetPartner, out critBonusMultiplier, out blocked);

                                if (finalDmg != 0 && !client.Tamer.GodMode)
                                {
                                    finalDmg = DebuffReductionDamage(client, finalDmg);
                                }
                                #endregion

                                #region Take Damage
                                if (finalDmg <= 0) finalDmg = 1;

                                var newHp = targetPartner.ReceiveDamage(finalDmg);

                                var hitType = blocked ? 2 : critBonusMultiplier > 0 ? 1 : 0;

                                if (newHp > 0)
                                {
                                    _logger.Verbose($"Partner {client.Partner.Id} inflicted {finalDmg} to partner {targetPartner?.Id} - {targetPartner?.Name}.");

                                    _mapServer.BroadcastForTamerViewsAndSelf(
                                        client.TamerId,
                                        new HitPacket(
                                            attackerHandler,
                                            targetHandler,
                                            finalDmg,
                                            targetPartner.HP,
                                            newHp,
                                            hitType).Serialize());
                                }
                                else
                                {
                                    _logger.Verbose($"Partner {client.Partner.Id} killed partner {targetPartner?.Id} - {targetPartner?.Name} with {finalDmg} damage.");

                                    _mapServer.BroadcastForTamerViewsAndSelf(
                                        client.TamerId,
                                        new KillOnHitPacket(
                                            attackerHandler,
                                            targetHandler,
                                            finalDmg,
                                            hitType).Serialize());

                                    targetPartner.Character.Die();

                                    if (!_mapServer.EnemiesAttacking(client.Tamer.Location.MapId, client.Partner.Id, client.TamerId))
                                    {
                                        client.Tamer.StopBattle();

                                        _mapServer.BroadcastForTamerViewsAndSelf(
                                            client.TamerId,
                                            new SetCombatOffPacket(attackerHandler).Serialize());
                                    }
                                }
                                #endregion

                                client.Partner.StartAutoAttack();
                            }

                            client.Tamer.Partner.UpdateLastHitTime();
                        }
                    }
                    else
                    {
                        if (!_mapServer.EnemiesAttacking(client.Tamer.Location.MapId, client.Partner.Id, client.TamerId))
                        {
                            client.Tamer.StopBattle();

                            _mapServer.BroadcastForTamerViewsAndSelf(
                                client.TamerId,
                                new SetCombatOffPacket(attackerHandler).Serialize());
                        }
                    }
                }
                else if (client.DungeonMap)
                {
                    // Summon
                    if (_dungeonServer.GetMobByHandler(client.Tamer.Location.MapId, targetHandler, true, client.TamerId) != null)
                    {
                        var targetMob = _dungeonServer.GetMobByHandler(client.Tamer.Location.MapId, targetHandler, true, client.TamerId);

                        if (targetMob == null || client.Partner == null)
                            return Task.CompletedTask;

                        if (DateTime.Now < client.Partner.LastHitTime.AddMilliseconds(client.Partner.AS))
                            client.Partner.StartAutoAttack();

                        if (targetMob.Alive)
                        {
                            if (client.Partner.IsAttacking)
                            {
                                if (client.Tamer.TargetMob?.GeneralHandler != targetMob.GeneralHandler)
                                {
                                    _logger.Verbose($"Character {client.Tamer.Id} switched target to {targetMob.Id} - {targetMob.Name}.");
                                    client.Tamer.SetHidden(false);
                                    client.Tamer.UpdateTarget(targetMob);
                                    client.Partner.StartAutoAttack();
                                }
                            }
                            else
                            {
                                if (DateTime.Now < client.Partner.LastHitTime.AddMilliseconds(client.Partner.AS))
                                {
                                    client.Partner.StartAutoAttack();
                                    return Task.CompletedTask;
                                }

                                client.Partner.SetEndAttacking();

                                if (!client.Tamer.InBattle)
                                {
                                    _logger.Verbose($"Character {client.Tamer.Id} engaged {targetMob.Id} - {targetMob.Name}.");
                                    client.Tamer.SetHidden(false);

                                    _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, new SetCombatOnPacket(attackerHandler).Serialize());
                                    client.Tamer.StartBattle(targetMob);
                                }
                                else
                                {
                                    _logger.Verbose($"Character {client.Tamer.Id} switched to {targetMob.Id} - {targetMob.Name}.");
                                    client.Tamer.SetHidden(false);
                                    client.Tamer.UpdateTarget(targetMob);
                                }

                                if (!targetMob.InBattle)
                                {
                                    _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, new SetCombatOnPacket(targetHandler).Serialize());
                                    targetMob.StartBattle(client.Tamer);
                                }
                                else
                                {
                                    targetMob.AddTarget(client.Tamer);
                                }

                                client.Tamer.Partner.StartAutoAttack();

                                var missed = false;

                                if (!client.Tamer.GodMode)
                                {
                                    missed = client.Tamer.CanMissHit(true);
                                }

                                if (missed)
                                {
                                    _logger.Verbose($"Partner {client.Tamer.Partner.Id} missed hit on {client.Tamer.TargetSummonMob.Id} - {client.Tamer.TargetSummonMob.Name}.");
                                    _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, new MissHitPacket(attackerHandler, targetHandler).Serialize());
                                }
                                else
                                {
                                    #region Hit Damage
                                    var critBonusMultiplier = 0.00;
                                    var blocked = false;
                                    var finalDmg = client.Tamer.GodMode ? targetMob.CurrentHP : CalculateFinalDamage(client, targetMob, out critBonusMultiplier, out blocked);

                                    if (finalDmg != 0 && !client.Tamer.GodMode)
                                    {
                                        finalDmg = DebuffReductionDamage(client, finalDmg);
                                    }
                                    #endregion

                                    #region Take Damage
                                    if (finalDmg <= 0) finalDmg = 1;
                                    if (finalDmg > targetMob.CurrentHP) finalDmg = targetMob.CurrentHP;

                                    var newHp = targetMob.ReceiveDamage(finalDmg, client.TamerId);

                                    var hitType = blocked ? 2 : critBonusMultiplier > 0 ? 1 : 0;

                                    if (newHp > 0)
                                    {
                                        _logger.Verbose($"Partner {client.Partner.Id} inflicted {finalDmg} to mob {targetMob?.Id} - {targetMob?.Name}({targetMob?.Type}).");

                                        _dungeonServer.BroadcastForTamerViewsAndSelf(
                                            client.TamerId,
                                            new HitPacket(
                                                attackerHandler,
                                                targetHandler,
                                                finalDmg,
                                                targetMob.HPValue,
                                                newHp,
                                                hitType).Serialize());
                                    }
                                    else
                                    {
                                        client.Partner.SetEndAttacking(client.Partner.AS * -2);

                                        _logger.Verbose($"Partner {client.Partner.Id} killed mob {targetMob?.Id} - {targetMob?.Name}({targetMob?.Type}) with {finalDmg} damage.");

                                        _dungeonServer.BroadcastForTamerViewsAndSelf(
                                            client.TamerId,
                                            new KillOnHitPacket(
                                                attackerHandler,
                                                targetHandler,
                                                finalDmg,
                                                hitType).Serialize());

                                        targetMob?.Die();

                                        if (!_dungeonServer.MobsAttacking(client.Tamer.Location.MapId, client.TamerId))
                                        {
                                            client.Tamer.StopBattle(true);

                                            _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, new SetCombatOffPacket(attackerHandler).Serialize());
                                        }
                                    }
                                    #endregion
                                }

                                client.Tamer.Partner.UpdateLastHitTime();
                            }
                        }
                        else
                        {
                            if (!_dungeonServer.MobsAttacking(client.Tamer.Location.MapId, client.TamerId, true))
                            {
                                client.Tamer.StopBattle(true);

                                _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, new SetCombatOffPacket(attackerHandler).Serialize());
                            }
                        }
                    }
                    else
                    {
                        var targetMob = _dungeonServer.GetMobByHandler(client.Tamer.Location.MapId, targetHandler, client.TamerId);

                        if (targetMob == null || client.Partner == null)
                            return Task.CompletedTask;

                        if (DateTime.Now < client.Partner.LastHitTime.AddMilliseconds(client.Partner.AS))
                            client.Partner.StartAutoAttack();

                        if (targetMob.Alive)
                        {
                            if (client.Partner.IsAttacking)
                            {
                                if (client.Tamer.TargetMob?.GeneralHandler != targetMob.GeneralHandler)
                                {
                                    _logger.Verbose($"Character {client.Tamer.Id} switched target to {targetMob.Id} - {targetMob.Name}.");
                                    client.Tamer.SetHidden(false);
                                    client.Tamer.UpdateTarget(targetMob);
                                    client.Partner.StartAutoAttack();
                                }
                            }
                            else
                            {
                                if (DateTime.Now < client.Partner.LastHitTime.AddMilliseconds(client.Partner.AS))
                                {
                                    client.Partner.StartAutoAttack();
                                    return Task.CompletedTask;
                                }

                                client.Partner.SetEndAttacking(client.Partner.AS);

                                if (!client.Tamer.InBattle)
                                {
                                    _logger.Verbose($"Character {client.Tamer.Id} engaged {targetMob.Id} - {targetMob.Name}.");
                                    client.Tamer.SetHidden(false);

                                    _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, new SetCombatOnPacket(attackerHandler).Serialize());
                                    client.Tamer.StartBattle(targetMob);
                                }
                                else
                                {
                                    _logger.Verbose($"Character {client.Tamer.Id} switched to {targetMob.Id} - {targetMob.Name}.");
                                    client.Tamer.SetHidden(false);
                                    client.Tamer.UpdateTarget(targetMob);
                                }

                                if (!targetMob.InBattle)
                                {
                                    _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, new SetCombatOnPacket(targetHandler).Serialize());
                                    targetMob.StartBattle(client.Tamer);
                                }
                                else
                                {
                                    targetMob.AddTarget(client.Tamer);
                                }

                                client.Tamer.Partner.StartAutoAttack();

                                var missed = false;

                                if (!client.Tamer.GodMode)
                                {
                                    missed = client.Tamer.CanMissHit();
                                }

                                if (missed)
                                {
                                    _logger.Verbose($"Partner {client.Tamer.Partner.Id} missed hit on {client.Tamer.TargetMob.Id} - {client.Tamer.TargetMob.Name}.");
                                    _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, new MissHitPacket(attackerHandler, targetHandler).Serialize());
                                }
                                else
                                {
                                    #region Hit Damage
                                    var critBonusMultiplier = 0.00;
                                    var blocked = false;
                                    var finalDmg = client.Tamer.GodMode ? targetMob.CurrentHP : CalculateFinalDamage(client, targetMob, out critBonusMultiplier, out blocked);

                                    if (finalDmg != 0 && !client.Tamer.GodMode)
                                    {
                                        finalDmg = DebuffReductionDamage(client, finalDmg);
                                    }
                                    #endregion

                                    #region Take Damage

                                    if (finalDmg <= 0) finalDmg = 1;
                                    if (finalDmg > targetMob.CurrentHP) finalDmg = targetMob.CurrentHP;

                                    var newHp = targetMob.ReceiveDamage(finalDmg, client.TamerId);

                                    var hitType = blocked ? 2 : critBonusMultiplier > 0 ? 1 : 0;

                                    if (newHp > 0)
                                    {
                                        _logger.Verbose($"Partner {client.Partner.Id} inflicted {finalDmg} to mob {targetMob?.Id} - {targetMob?.Name}({targetMob?.Type}).");

                                        _dungeonServer.BroadcastForTamerViewsAndSelf(
                                            client.TamerId,
                                            new HitPacket(
                                                attackerHandler,
                                                targetHandler,
                                                finalDmg,
                                                targetMob.HPValue,
                                                newHp,
                                                hitType).Serialize());
                                    }
                                    else
                                    {
                                        client.Partner.SetEndAttacking(client.Partner.AS * -2);

                                        _logger.Verbose($"Partner {client.Partner.Id} killed mob {targetMob?.Id} - {targetMob?.Name}({targetMob?.Type}) with {finalDmg} damage.");

                                        _dungeonServer.BroadcastForTamerViewsAndSelf(
                                            client.TamerId,
                                            new KillOnHitPacket(
                                                attackerHandler,
                                                targetHandler,
                                                finalDmg,
                                                hitType).Serialize());

                                        targetMob?.Die();

                                        if (!_dungeonServer.MobsAttacking(client.Tamer.Location.MapId, client.TamerId))
                                        {
                                            client.Tamer.StopBattle();

                                            _dungeonServer.BroadcastForTamerViewsAndSelf(
                                                client.TamerId,
                                                new SetCombatOffPacket(attackerHandler).Serialize());
                                        }
                                    }
                                    #endregion
                                }

                                client.Tamer.Partner.UpdateLastHitTime();
                            }
                        }
                        else
                        {
                            if (!_dungeonServer.MobsAttacking(client.Tamer.Location.MapId, client.TamerId))
                            {
                                client.Tamer.StopBattle();

                                _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new SetCombatOffPacket(attackerHandler).Serialize());
                            }
                        }
                    }

                }
                else if (_mapServer.GetMobByHandler(client.Tamer.Location.MapId, targetHandler, true, client.TamerId) != null) //Summon
                {
                    var targetMob = _mapServer.GetMobByHandler(client.Tamer.Location.MapId, targetHandler, true, client.TamerId);

                    if (targetMob == null || client.Partner == null)
                        return Task.CompletedTask;

                    if (DateTime.Now < client.Partner.LastHitTime.AddMilliseconds(client.Partner.AS))
                        client.Partner.StartAutoAttack();

                    if (targetMob.Alive)
                    {
                        if (client.Partner.IsAttacking)
                        {
                            if (client.Tamer.TargetSummonMob?.GeneralHandler != targetMob.GeneralHandler)
                            {
                                _logger.Verbose($"Character {client.Tamer.Id} switched target to {targetMob.Id} - {targetMob.Name}.");
                                client.Tamer.SetHidden(false);
                                client.Tamer.UpdateTarget(targetMob);
                                client.Partner.StartAutoAttack();
                            }
                        }
                        else
                        {
                            if (DateTime.Now < client.Partner.LastHitTime.AddMilliseconds(client.Partner.AS))
                            {
                                client.Partner.StartAutoAttack();
                                return Task.CompletedTask;
                            }

                            client.Partner.SetEndAttacking();

                            if (!client.Tamer.InBattle)
                            {
                                _logger.Verbose($"Character {client.Tamer.Id} engaged {targetMob.Id} - {targetMob.Name}.");
                                client.Tamer.SetHidden(false);

                                _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new SetCombatOnPacket(attackerHandler).Serialize());
                                client.Tamer.StartBattle(targetMob);
                            }
                            else
                            {
                                _logger.Verbose($"Character {client.Tamer.Id} switched to {targetMob.Id} - {targetMob.Name}.");
                                client.Tamer.SetHidden(false);
                                client.Tamer.UpdateTarget(targetMob);
                            }

                            if (!targetMob.InBattle)
                            {
                                _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new SetCombatOnPacket(targetHandler).Serialize());
                                targetMob.StartBattle(client.Tamer);
                            }
                            else
                            {
                                targetMob.AddTarget(client.Tamer);
                            }

                            client.Tamer.Partner.StartAutoAttack();

                            var missed = false;

                            if (!client.Tamer.GodMode)
                            {
                                missed = client.Tamer.CanMissHit(true);
                            }

                            if (missed)
                            {
                                _logger.Verbose($"Partner {client.Tamer.Partner.Id} missed hit on {client.Tamer.TargetSummonMob.Id} - {client.Tamer.TargetSummonMob.Name}.");
                                _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new MissHitPacket(attackerHandler, targetHandler).Serialize());
                            }
                            else
                            {
                                #region Hit Damage
                                var critBonusMultiplier = 0.00;
                                var blocked = false;
                                var finalDmg = client.Tamer.GodMode ? targetMob.CurrentHP : CalculateFinalDamage(client, targetMob, out critBonusMultiplier, out blocked);

                                if (finalDmg != 0 && !client.Tamer.GodMode)
                                {
                                    finalDmg = DebuffReductionDamage(client, finalDmg);
                                }
                                #endregion

                                #region Take Damage
                                if (finalDmg <= 0) finalDmg = 1;
                                if (finalDmg > targetMob.CurrentHP) finalDmg = targetMob.CurrentHP;

                                var newHp = targetMob.ReceiveDamage(finalDmg, client.TamerId);

                                var hitType = blocked ? 2 : critBonusMultiplier > 0 ? 1 : 0;

                                if (newHp > 0)
                                {
                                    _logger.Verbose($"Partner {client.Partner.Id} inflicted {finalDmg} to mob {targetMob?.Id} - {targetMob?.Name}({targetMob?.Type}).");

                                    _mapServer.BroadcastForTamerViewsAndSelf(
                                        client.TamerId,
                                        new HitPacket(
                                            attackerHandler,
                                            targetHandler,
                                            finalDmg,
                                            targetMob.HPValue,
                                            newHp,
                                            hitType).Serialize());
                                }
                                else
                                {
                                    client.Partner.SetEndAttacking(client.Partner.AS * -2);

                                    _logger.Verbose($"Partner {client.Partner.Id} killed mob {targetMob?.Id} - {targetMob?.Name}({targetMob?.Type}) with {finalDmg} damage.");

                                    _mapServer.BroadcastForTamerViewsAndSelf(
                                        client.TamerId,
                                        new KillOnHitPacket(
                                            attackerHandler,
                                            targetHandler,
                                            finalDmg,
                                            hitType).Serialize());

                                    targetMob?.Die();

                                    if (!_mapServer.MobsAttacking(client.Tamer.Location.MapId, client.TamerId))
                                    {
                                        client.Tamer.StopBattle(true);

                                        _mapServer.BroadcastForTamerViewsAndSelf(
                                            client.TamerId,
                                            new SetCombatOffPacket(attackerHandler).Serialize());
                                    }
                                }
                                #endregion
                            }

                            client.Tamer.Partner.UpdateLastHitTime();
                        }
                    }
                    else
                    {
                        if (!_mapServer.MobsAttacking(client.Tamer.Location.MapId, client.TamerId, true))
                        {
                            client.Tamer.StopBattle(true);

                            _mapServer.BroadcastForTamerViewsAndSelf(
                                client.TamerId,
                                new SetCombatOffPacket(attackerHandler).Serialize());
                        }
                    }
                }
                else
                {
                    var targetMob = _mapServer.GetMobByHandler(client.Tamer.Location.MapId, targetHandler, client.TamerId);

                    if (targetMob == null || client.Partner == null)
                        return Task.CompletedTask;

                    if (DateTime.Now < client.Partner.LastHitTime.AddMilliseconds(client.Partner.AS))
                        client.Partner.StartAutoAttack();

                    if (targetMob.Alive)
                    {
                        if (client.Partner.IsAttacking)
                        {
                            if (client.Tamer.TargetMob?.GeneralHandler != targetMob.GeneralHandler)
                            {
                                _logger.Verbose($"Character {client.Tamer.Id} switched target to {targetMob.Id} - {targetMob.Name}.");
                                client.Tamer.SetHidden(false);
                                client.Tamer.UpdateTarget(targetMob);
                                client.Partner.StartAutoAttack();
                            }
                        }
                        else
                        {
                            if (DateTime.Now < client.Partner.LastHitTime.AddMilliseconds(client.Partner.AS))
                            {
                                client.Partner.StartAutoAttack();
                                return Task.CompletedTask;
                            }

                            client.Partner.SetEndAttacking();

                            if (!client.Tamer.InBattle)
                            {
                                _logger.Verbose($"Character {client.Tamer.Id} engaged {targetMob.Id} - {targetMob.Name}.");
                                client.Tamer.SetHidden(false);

                                _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new SetCombatOnPacket(attackerHandler).Serialize());
                                client.Tamer.StartBattle(targetMob);
                            }
                            else
                            {
                                _logger.Verbose($"Character {client.Tamer.Id} switched to {targetMob.Id} - {targetMob.Name}.");
                                client.Tamer.SetHidden(false);
                                client.Tamer.UpdateTarget(targetMob);
                            }

                            if (!targetMob.InBattle)
                            {
                                _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new SetCombatOnPacket(targetHandler).Serialize());
                                targetMob.StartBattle(client.Tamer);
                            }
                            else
                            {
                                targetMob.AddTarget(client.Tamer);
                            }

                            client.Tamer.Partner.StartAutoAttack();

                            var missed = false;

                            if (!client.Tamer.GodMode)
                            {
                                missed = client.Tamer.CanMissHit();
                            }

                            if (missed)
                            {
                                _logger.Verbose($"Partner {client.Tamer.Partner.Id} missed hit on {client.Tamer.TargetMob.Id} - {client.Tamer.TargetMob.Name}.");
                                _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new MissHitPacket(attackerHandler, targetHandler).Serialize());
                            }
                            else
                            {
                                #region Hit Damage
                                var critBonusMultiplier = 0.00;
                                var blocked = false;
                                var finalDmg = client.Tamer.GodMode ? targetMob.CurrentHP : CalculateFinalDamage(client, targetMob, out critBonusMultiplier, out blocked);

                                if (finalDmg != 0 && !client.Tamer.GodMode)
                                {
                                    finalDmg = DebuffReductionDamage(client, finalDmg);
                                }
                                #endregion

                                #region Take Damage
                                if (finalDmg <= 0) finalDmg = 1;
                                if (finalDmg > targetMob.CurrentHP) finalDmg = targetMob.CurrentHP;

                                var newHp = targetMob.ReceiveDamage(finalDmg, client.TamerId);

                                var hitType = blocked ? 2 : critBonusMultiplier > 0 ? 1 : 0;

                                if (newHp > 0)
                                {
                                    _logger.Verbose($"Partner {client.Partner.Id} inflicted {finalDmg} to mob {targetMob?.Id} - {targetMob?.Name}({targetMob?.Type}).");

                                    _mapServer.BroadcastForTamerViewsAndSelf(
                                        client.TamerId,
                                        new HitPacket(
                                            attackerHandler,
                                            targetHandler,
                                            finalDmg,
                                            targetMob.HPValue,
                                            newHp,
                                            hitType).Serialize());
                                }
                                else
                                {
                                    client.Partner.SetEndAttacking(client.Partner.AS * -2);

                                    _logger.Verbose($"Partner {client.Partner.Id} killed mob {targetMob?.Id} - {targetMob?.Name}({targetMob?.Type}) with {finalDmg} damage.");

                                    _mapServer.BroadcastForTamerViewsAndSelf(
                                        client.TamerId,
                                        new KillOnHitPacket(
                                            attackerHandler,
                                            targetHandler,
                                            finalDmg,
                                            hitType).Serialize());

                                    targetMob?.Die();

                                    if (!_mapServer.MobsAttacking(client.Tamer.Location.MapId, client.TamerId))
                                    {
                                        client.Tamer.StopBattle();

                                        _mapServer.BroadcastForTamerViewsAndSelf(
                                            client.TamerId,
                                            new SetCombatOffPacket(attackerHandler).Serialize());
                                    }
                                }
                                #endregion
                            }

                            client.Tamer.Partner.UpdateLastHitTime();
                        }
                    }
                    else
                    {
                        if (!_mapServer.MobsAttacking(client.Tamer.Location.MapId, client.TamerId))
                        {
                            client.Tamer.StopBattle();

                            _mapServer.BroadcastForTamerViewsAndSelf(
                                client.TamerId,
                                new SetCombatOffPacket(attackerHandler).Serialize());
                        }
                    }
                }

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.Error($"[{client.Tamer.Name}] An error occurred: {ex.Message}", ex);
                client.Disconnect();
            }

            return Task.CompletedTask;
        }

        private static int DebuffReductionDamage(GameClient client, int finalDmg)
        {
            if (client.Tamer.Partner.DebuffList.ActiveDebuffReductionDamage())
            {
                var debuffInfo = client.Tamer.Partner.DebuffList.ActiveBuffs
                .Where(buff => buff.BuffInfo.SkillInfo.Apply
                    .Any(apply => apply.Attribute == Commons.Enums.SkillCodeApplyAttributeEnum.AttackPowerDown))

                .ToList();

                var totalValue = 0;
                var SomaValue = 0;

                foreach (var debuff in debuffInfo)
                {
                    foreach (var apply in debuff.BuffInfo.SkillInfo.Apply)
                    {

                        switch (apply.Type)
                        {
                            case SkillCodeApplyTypeEnum.Default:
                                totalValue += apply.Value;
                                break;

                            case SkillCodeApplyTypeEnum.AlsoPercent:
                            case SkillCodeApplyTypeEnum.Percent:
                                {

                                    SomaValue += apply.Value + (debuff.TypeN) * apply.IncreaseValue;

                                    double fatorReducao = SomaValue / 100;

                                    // Calculando o novo finalDmg após a redução
                                    finalDmg -= (int)(finalDmg * fatorReducao);

                                }
                                break;

                            case SkillCodeApplyTypeEnum.Unknown200:
                                {

                                    SomaValue += apply.AdditionalValue;

                                    double fatorReducao = SomaValue / 100.0;

                                    // Calculando o novo finalDmg após a redução
                                    finalDmg -= (int)(finalDmg * fatorReducao);

                                }
                                break;

                        }
                    }
                }
            }

            return finalDmg;
        }

        // --------------------------------------------------------------------------------------------------------------------

        // Mob
        private static int CalculateFinalDamage(GameClient client, MobConfigModel? targetMob, out double critBonusMultiplier, out bool blocked)
        {
            int baseDamage = client.Tamer.Partner.AT - targetMob.DEValue;

            if (baseDamage < client.Tamer.Partner.AT * 0.5) // If Damage is less than 50% of AT
            {
                baseDamage = (int)(client.Tamer.Partner.AT * 0.6); // give 60% of AT as Damage
            }

            // -------------------------------------------------------------------------------

            critBonusMultiplier = 0.00;
            double critChance = client.Tamer.Partner.CC / 100;

            if (critChance >= UtilitiesFunctions.RandomDouble())
            {
                blocked = false;

                var critDamageMultiplier = client.Tamer.Partner.CD / 100.0;
                critBonusMultiplier = baseDamage * (critDamageMultiplier / 100);
            }

            // -------------------------------------------------------------------------------

            if (client.Tamer.TargetMob != null)
            {
                blocked = targetMob.BLValue >= UtilitiesFunctions.RandomDouble();
            }
            else
            {
                blocked = false;
                return 0;
            }

            // -------------------------------------------------------------------------------

            // Level Diference
            var levelBonus = 0.0;
            //var levelDifference = client.Tamer.Partner.Level - targetMob.Level;
            //levelBonus = levelDifference > 0 ? baseDamage * 0.05 : baseDamage * -0.1;

            // Attribute
            var attributeMultiplier = 0.00;
            if (client.Tamer.Partner.BaseInfo.Attribute.HasAttributeAdvantage(targetMob.Attribute))
            {
                var attExp = client.Tamer.Partner.GetAttributeExperience();
                var attValue = client.Partner.ATT / 100.0;
                var attValuePercent = attValue / 100.0;
                var bonusMax = 0.5;
                var expMax = 10000;

                attributeMultiplier = ((bonusMax + attValuePercent) * attExp) / expMax;
            }
            else if (targetMob.Attribute.HasAttributeAdvantage(client.Tamer.Partner.BaseInfo.Attribute))
            {
                attributeMultiplier -= 0.25;
            }

            // Element
            var elementMultiplier = 0.00;
            var elDamage = 0.0;
            if (client.Tamer.Partner.BaseInfo.Element.HasElementAdvantage(targetMob.Element))
            {
                var vlrAtual = client.Tamer.Partner.GetElementExperience();
                var bonusMax = 0.5;
                var expMax = 10000;

                elementMultiplier = (bonusMax * vlrAtual) / expMax;
            }
            else if (targetMob.Element.HasElementAdvantage(client.Tamer.Partner.BaseInfo.Element))
            {
                elementMultiplier -= 0.25;
            }

            // -------------------------------------------------------------------------------

            if (blocked)
                baseDamage /= 2;

            int finalDamage = (int)Math.Max(1, Math.Floor(baseDamage + critBonusMultiplier + levelBonus +
                (baseDamage * attributeMultiplier) + (baseDamage * elementMultiplier)));

            //Console.WriteLine($"BaseDamage: {baseDamage} | critBonusMultiplier: {critBonusMultiplier} | LevelBonus: {levelBonus}");
            //Console.WriteLine($"Attribute: {baseDamage * attributeMultiplier} | Element: {baseDamage * elementMultiplier}");
            //Console.WriteLine($"FinalDamage: {finalDamage}");

            return finalDamage;
        }

        // Summon
        private static int CalculateFinalDamage(GameClient client, SummonMobModel targetSummonMob, out double critBonusMultiplier, out bool blocked)
        {
            int baseDamage = client.Tamer.Partner.AT - targetSummonMob.DEValue;

            if (baseDamage < client.Tamer.Partner.AT * 0.5) // If Damage is less than 50% of AT
            {
                baseDamage = (int)(client.Tamer.Partner.AT * 0.6);
            }

            // -------------------------------------------------------------------------------

            critBonusMultiplier = 0.00;
            double critChance = client.Tamer.Partner.CC / 100;

            // Critical
            if (critChance >= UtilitiesFunctions.RandomDouble())
            {
                blocked = false;

                var critDamageMultiplier = client.Tamer.Partner.CD / 100.0;
                critBonusMultiplier = baseDamage * (critDamageMultiplier / 100);
            }

            // Block
            if (targetSummonMob != null)
            {
                blocked = targetSummonMob.BLValue >= UtilitiesFunctions.RandomDouble();
            }
            else
            {
                blocked = false;
                return 0;
            }

            // -------------------------------------------------------------------------------

            // Level
            var levelBonusMultiplier = 0;

            // Atributte
            var attributeMultiplier = 0.00;
            if (client.Tamer.Partner.BaseInfo.Attribute.HasAttributeAdvantage(targetSummonMob.Attribute))
            {
                var attExp = client.Tamer.Partner.GetAttributeExperience();
                var attValue = client.Partner.ATT / 100.0;
                var attValuePercent = attValue / 100.0;
                var bonusMax = 0.5;
                var expMax = 10000;

                attributeMultiplier = ((bonusMax + attValuePercent) * attExp) / expMax;
            }
            else if (targetSummonMob.Attribute.HasAttributeAdvantage(client.Tamer.Partner.BaseInfo.Attribute))
            {
                attributeMultiplier = -0.25;
            }

            // Element
            var elementMultiplier = 0.00;
            if (client.Tamer.Partner.BaseInfo.Element.HasElementAdvantage(targetSummonMob.Element))
            {
                var vlrAtual = client.Tamer.Partner.GetElementExperience();
                var bonusMax = 0.5;
                var expMax = 10000;

                elementMultiplier = (bonusMax * vlrAtual) / expMax;
            }
            else if (targetSummonMob.Element.HasElementAdvantage(client.Tamer.Partner.BaseInfo.Element))
            {
                elementMultiplier = -0.25;
            }

            if (blocked)
                baseDamage /= 2;

            return (int)Math.Max(1, Math.Floor(baseDamage + critBonusMultiplier +
                (baseDamage * levelBonusMultiplier) + (baseDamage * attributeMultiplier) + (baseDamage * elementMultiplier)));
        }

        // Player
        private static int CalculateFinalDamage(GameClient client, DigimonModel? targetPartner, out double critBonusMultiplier, out bool blocked)
        {
            var baseDamage = (client.Tamer.Partner.AT / targetPartner.DE * 150) + UtilitiesFunctions.RandomInt(5, 50);
            if (baseDamage < 0) baseDamage = 0;

            critBonusMultiplier = 0.00;
            double critChance = client.Tamer.Partner.CC / 100;

            if (critChance >= UtilitiesFunctions.RandomDouble())
            {

                var vlrAtual = client.Tamer.Partner.CD;
                var bonusMax = 1.50; //TODO: externalizar?
                var expMax = 10000; //TODO: externalizar?

                critBonusMultiplier = (bonusMax * vlrAtual) / expMax;
            }

            blocked = targetPartner.BL >= UtilitiesFunctions.RandomDouble();

            var levelBonusMultiplier = client.Tamer.Partner.Level > targetPartner.Level ? (0.01f * (client.Tamer.Partner.Level - targetPartner.Level)) : 0;

            var attributeMultiplier = 0.00;

            if (client.Tamer.Partner.BaseInfo.Attribute.HasAttributeAdvantage(targetPartner.BaseInfo.Attribute))
            {
                var vlrAtual = client.Tamer.Partner.GetAttributeExperience();
                var bonusMax = 1.00; //TODO: externalizar?
                var expMax = 10000; //TODO: externalizar?

                attributeMultiplier = (bonusMax * vlrAtual) / expMax;
            }
            else if (targetPartner.BaseInfo.Attribute.HasAttributeAdvantage(client.Tamer.Partner.BaseInfo.Attribute))
            {
                attributeMultiplier = -0.25;
            }

            var elementMultiplier = 0.00;
            if (client.Tamer.Partner.BaseInfo.Element.HasElementAdvantage(targetPartner.BaseInfo.Element))
            {
                var vlrAtual = client.Tamer.Partner.GetElementExperience();
                var bonusMax = 0.50; //TODO: externalizar?
                var expMax = 10000; //TODO: externalizar?

                elementMultiplier = (bonusMax * vlrAtual) / expMax;
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

        // --------------------------------------------------------------------------------------------------------------------
    }
}