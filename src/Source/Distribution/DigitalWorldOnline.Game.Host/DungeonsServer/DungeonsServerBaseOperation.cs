using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Application.Separar.Queries;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Models;
using DigitalWorldOnline.Commons.Models.Character;
using DigitalWorldOnline.Commons.Models.Config;
using DigitalWorldOnline.Commons.Models.Map;
using DigitalWorldOnline.Commons.Models.Map.Dungeons;
using DigitalWorldOnline.Commons.Models.Summon;
using DigitalWorldOnline.Commons.Models.TamerShop;
using DigitalWorldOnline.Commons.Models.Mechanics; // GameParty
using System.Diagnostics;
using System.Threading;

namespace DigitalWorldOnline.GameHost
{
    public sealed partial class DungeonsServer
    {
        private DateTime _lastMapsSearch = DateTime.Now;
        private DateTime _lastMobsSearch = DateTime.Now;
        private DateTime _lastConsignedShopsSearch = DateTime.Now;

        //TODO: externalizar
        private readonly int _startToSee = 6000;
        private readonly int _stopSeeing = 6001;

        private readonly object _mapsLock = new(); // lock apenas para alterações em Maps

        /// <summary>
        /// Cleans unused running maps.
        /// </summary>
        public Task CleanMaps()
        {
            List<GameMap> snapshot;
            lock (_mapsLock)
            {
                snapshot = Maps.Where(x => x.CloseMap).ToList();
                foreach (var m in snapshot)
                    _logger.Information($"Removing inactive instance for {m.Type} map {m.Id} - {m.Name}...");

                Maps.RemoveAll(x => x.CloseMap);
            }

            return Task.CompletedTask;
        }

        public Task CleanMap(int dungeonId)
        {
            lock (_mapsLock)
            {
                var mapToClose = Maps.FirstOrDefault(x => x.DungeonId == dungeonId);
                if (mapToClose != null)
                {
                    _logger.Information($"Removing inactive instance for {mapToClose.Type} map {mapToClose.Id} - {mapToClose.Name}...");
                    Maps.Remove(mapToClose);
                }
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Search for new maps to instance (varredura em background).
        /// </summary>
        public async Task SearchNewMaps(CancellationToken cancellationToken)
        {
            if (DateTime.Now <= _lastMapsSearch) return;

            var mapsToLoad = _mapper.Map<List<GameMap>>(
                await _sender.Send(new GameMapsConfigQuery(MapTypeEnum.Dungeon), cancellationToken));

            var parties = (_partyManager.Parties ?? Array.Empty<GameParty>()).ToList();

            foreach (var newMap in mapsToLoad)
            {
                foreach (var party in parties)
                {
                    // só instancia se for o mesmo mapa que a party está usando
                    var leaderEntry = party.Members.FirstOrDefault(x => x.Key == party.LeaderId);
                    var leaderMapId = leaderEntry.Value?.Location?.MapId ?? 0;

                    bool shouldInstance = leaderMapId == newMap.MapId;

                    if (!shouldInstance)
                        continue;

                    bool alreadyHasInstance;
                    lock (_mapsLock)
                    {
                        alreadyHasInstance = Maps.Any(x => x.DungeonId == party.Id);
                    }
                    if (alreadyHasInstance)
                        continue;

                    _logger.Debug($"Initializing new instance for {newMap.Type} map {newMap.Id} - {newMap.Name}...");

                    int[] RoyalBaseMaps = { 1701, 1702, 1703 };

                    if (Array.Exists(RoyalBaseMaps, element => element == newMap.MapId))
                    {
                        var royalBaseMap = new RoyalBaseMap((short)newMap.MapId, newMap.Mobs);
                        newMap.IsRoyalBaseUpdate(true);
                        newMap.setRoyalBaseMap(royalBaseMap);
                    }
                    else
                    {
                        newMap.IsRoyalBaseUpdate(false);
                        newMap.setRoyalBaseMap(null);
                    }

                    lock (_mapsLock)
                    {
                        Maps.Add(newMap);
                    }
                }
            }

            _lastMapsSearch = DateTime.Now.AddSeconds(10);
        }

        public async Task SearchNewMaps(bool isParty, GameClient client)
        {
            var mapsToLoad = _mapper.Map<List<GameMap>>(
                await _sender.Send(new GameMapsConfigQuery(MapTypeEnum.Dungeon)));

            if (isParty)
            {
                var party = _partyManager.FindParty(client.TamerId);
                if (party == null) return;

                foreach (var baseMap in mapsToLoad)
                {
                    bool alreadyHasInstance;
                    lock (_mapsLock)
                    {
                        alreadyHasInstance = Maps.Exists(x => x.DungeonId == party.Id);
                    }
                    if (alreadyHasInstance || baseMap.MapId != client.Tamer.Location.MapId)
                        continue;

                    var newDungeon = (GameMap)baseMap.Clone();

                    // Remove mobs de coliseu com round > 0
                    var mobsToRemove = newDungeon.Mobs.Where(x => x.Coliseum && x.Round > 0).ToList();
                    foreach (var m in mobsToRemove)
                        newDungeon.Mobs.Remove(m);

                    // Mapas do dia (ex.: 2001/2002)
                    if (baseMap.MapId == 2001 || baseMap.MapId == 2002)
                    {
                        var weekRemove = newDungeon.Mobs.Where(x => x.WeekDay != (DungeonDayOfWeekEnum)DateTime.Now.DayOfWeek).ToList();
                        foreach (var m in weekRemove)
                            newDungeon.Mobs.Remove(m);
                    }

                    newDungeon.SetId(party.Id);
                    _logger.Debug($"Initializing new instance for {baseMap.Type} party {party.Id} - {baseMap.Name}...");

                    int[] RoyalBaseMaps = { 1701, 1702, 1703 };
                    if (Array.Exists(RoyalBaseMaps, element => element == newDungeon.MapId))
                    {
                        var royalBaseMap = new RoyalBaseMap((short)newDungeon.MapId, newDungeon.Mobs);
                        newDungeon.IsRoyalBaseUpdate(true);
                        newDungeon.setRoyalBaseMap(royalBaseMap);
                    }
                    else
                    {
                        newDungeon.IsRoyalBaseUpdate(false);
                        newDungeon.setRoyalBaseMap(null);
                    }

                    lock (_mapsLock)
                    {
                        Maps.Add(newDungeon);
                    }
                }
            }
            else
            {
                foreach (var baseMap in mapsToLoad)
                {
                    bool alreadyHasInstance;
                    lock (_mapsLock)
                    {
                        alreadyHasInstance = Maps.Exists(x => x.DungeonId == client.TamerId);
                    }
                    if (alreadyHasInstance || baseMap.MapId != client.Tamer.Location.MapId)
                        continue;

                    var newDungeon = (GameMap)baseMap.Clone();

                    var mobsToRemove = newDungeon.Mobs.Where(x => x.Coliseum && x.Round > 0).ToList();
                    foreach (var m in mobsToRemove)
                        newDungeon.Mobs.Remove(m);

                    if (baseMap.MapId == 2001 || baseMap.MapId == 2002)
                    {
                        var weekRemove = newDungeon.Mobs.Where(x => x.WeekDay != (DungeonDayOfWeekEnum)DateTime.Now.DayOfWeek).ToList();
                        foreach (var m in weekRemove)
                            newDungeon.Mobs.Remove(m);
                    }

                    newDungeon.SetId((int)client.TamerId);
                    _logger.Debug($"Initializing new instance for {baseMap.Type} tamer {client.TamerId} - {baseMap.Name}...");

                    int[] RoyalBaseMaps = { 1701, 1702, 1703 };
                    if (Array.Exists(RoyalBaseMaps, element => element == newDungeon.MapId))
                    {
                        var royalBaseMap = new RoyalBaseMap((short)newDungeon.MapId, newDungeon.Mobs);
                        newDungeon.IsRoyalBaseUpdate(true);
                        newDungeon.setRoyalBaseMap(royalBaseMap);
                    }
                    else
                    {
                        newDungeon.IsRoyalBaseUpdate(false);
                        newDungeon.setRoyalBaseMap(null);
                    }

                    lock (_mapsLock)
                    {
                        Maps.Add(newDungeon);
                    }
                }
            }
        }

        /// <summary>
        /// Gets the maps objects.
        /// </summary>
        public async Task GetMapObjects(CancellationToken cancellationToken)
        {
            await GetMapMobs(cancellationToken);
        }

        public async Task GetMapObjects()
        {
            await GetMapMobs();
        }

        /// <summary>
        /// Gets the map latest mobs.
        /// </summary>
        private async Task GetMapMobs(CancellationToken cancellationToken)
        {
            List<GameMap> maps;
            lock (_mapsLock) { maps = Maps.Where(x => x.Initialized).ToList(); }

            foreach (var map in maps)
            {
                var mapMobs = _mapper.Map<IList<MobConfigModel>>(
                    await _sender.Send(new MapMobConfigsQuery(map.Id), cancellationToken));

                if (mapMobs != null)
                {
                    var toRemove = mapMobs.Where(x => x.Coliseum && x.Round > 0).ToList();
                    foreach (var m in toRemove)
                        mapMobs.Remove(m);
                }

                if (map.RequestMobsUpdate(mapMobs))
                    map.UpdateMobsList();
            }
        }

        private async Task GetMapMobs()
        {
            List<GameMap> maps;
            lock (_mapsLock) { maps = Maps.Where(x => x.Initialized).ToList(); }

            foreach (var map in maps)
            {
                var mapMobs = _mapper.Map<IList<MobConfigModel>>(
                    await _sender.Send(new MapMobConfigsQuery(map.Id)));

                if (mapMobs != null)
                {
                    var toRemove = mapMobs.Where(x => x.Coliseum && x.Round > 0).ToList();
                    foreach (var m in toRemove)
                        mapMobs.Remove(m);
                }

                if (map.RequestMobsUpdate(mapMobs))
                    map.UpdateMobsList();
            }
        }

        /// <summary>
        /// Gets the consigned shops latest list.
        /// </summary>
        private async Task GetMapConsignedShops(CancellationToken cancellationToken)
        {
            if (DateTime.Now <= _lastConsignedShopsSearch)
                return;

            List<GameMap> maps;
            lock (_mapsLock) { maps = Maps.Where(x => x.Initialized).ToList(); }

            foreach (var map in maps)
            {
                if (map.Operating)
                    continue;

                var consignedShops = _mapper.Map<List<ConsignedShop>>(
                    await _sender.Send(new ConsignedShopsQuery((int)map.Id), cancellationToken));

                map.UpdateConsignedShops(consignedShops);
            }

            _lastConsignedShopsSearch = DateTime.Now.AddSeconds(15);
        }

        /// <summary>
        /// The default hosted service "starting" method.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await CleanMaps();
                    await GetMapObjects(cancellationToken);

                    List<GameMap> snapshot;
                    lock (_mapsLock) { snapshot = Maps.ToList(); }

                    var tasks = new List<Task>();
                    snapshot.ForEach(map => tasks.Add(RunMap(map)));

                    await Task.WhenAll(tasks);

                    await Task.Delay(500, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Unexpected map exception: {ex.Message} {ex.StackTrace}");
                    await Task.Delay(3000, cancellationToken);
                }
            }
        }

        /// <summary>
        /// Runs the target map operations.
        /// </summary>
        private async Task RunMap(GameMap map)
        {
            try
            {
                map.Initialize();
                map.ManageHandlers();

                var sw = new Stopwatch();
                sw.Start();

                var tasks = new List<Task>
                {
                    Task.Run(() => TamerOperation(map)),
                    Task.Run(() => MonsterOperation(map)),
                    Task.Run(() => DropsOperation(map))
                };

                await Task.WhenAll(tasks);

                sw.Stop();
                var totalTime = sw.Elapsed.TotalMilliseconds;
                if (totalTime >= 1000)
                    Console.WriteLine($"RunMap ({map.MapId}): {totalTime}.");

                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                _logger.Error($"Unexpected error at map running: {ex.Message} {ex.StackTrace}.");
            }
        }

        /// <summary>
        /// Adds a new gameclient to the target map.
        /// </summary>
        public async Task AddClient(GameClient client)
        {
            if (client?.Tamer == null) return;

            if (client.Tamer.TargetTamerIdTP > 0)
            {
                GameMap map;
                lock (_mapsLock)
                {
                    map = Maps.FirstOrDefault(x => x.Clients.Exists(c => c.TamerId == client.Tamer.TargetTamerIdTP));
                }

                if (map == null)
                {
                    client.Tamer.SetTamerTP(0);
                    await _sender.Send(new ChangeTamerIdTPCommand(client.Tamer.Id, 0));
                    client.Disconnect();
                    return;
                }

                client.SetLoading();
                client.Tamer.MobsInView.Clear();
                map.AddClient(client);
                client.Tamer.Revive();
                return;
            }

            var party = _partyManager.FindParty(client.TamerId);
            if (party != null)
            {
                GameMap partyMap;
                lock (_mapsLock)
                {
                    partyMap = Maps.FirstOrDefault(x =>
                        x.Initialized &&
                        (
                            (x.DungeonId == party.LeaderId && x.MapId == client.Tamer.Location.MapId) ||
                            (x.DungeonId == party.Id && x.MapId == client.Tamer.Location.MapId)
                        ));
                }

                if (partyMap != null)
                {
                    client.SetLoading();
                    client.Tamer.MobsInView.Clear();
                    partyMap.AddClient(client);
                    client.Tamer.Revive();
                }
                else
                {
                    await SearchNewMaps(true, client);

                    while (partyMap == null)
                    {
                        lock (_mapsLock)
                        {
                            partyMap = Maps.FirstOrDefault(x =>
                                x.Initialized &&
                                (x.DungeonId == party.LeaderId || x.DungeonId == party.Id) &&
                                x.MapId == client.Tamer.Location.MapId);
                        }

                        if (partyMap != null)
                        {
                            client.Tamer.MobsInView.Clear();
                            partyMap.AddClient(client);
                            client.Tamer.Revive();
                            return;
                        }

                        await Task.Delay(1000);
                    }
                }
            }
            else
            {
                await SearchNewMaps(false, client);

                GameMap map;
                lock (_mapsLock)
                {
                    map = Maps.FirstOrDefault(x => x.Initialized && x.DungeonId == client.Tamer.Id);
                }

                if (map != null)
                {
                    client.Tamer.MobsInView.Clear();
                    map.AddClient(client);
                    client.Tamer.Revive();
                }
                else
                {
                    await Task.Run(async () =>
                    {
                        GameMap inner = null;
                        while (inner == null)
                        {
                            await Task.Delay(1000);
                            lock (_mapsLock)
                            {
                                inner = Maps.FirstOrDefault(x => x.Initialized && x.DungeonId == client.Tamer.Id);
                            }
                        }

                        client.Tamer.MobsInView.Clear();
                        inner.AddClient(client);
                        client.Tamer.Revive();
                    });
                }
            }
        }

        /// <summary>
        /// Removes the gameclient from the target map.
        /// </summary>
        public void RemoveClient(GameClient client)
        {
            if (client == null) return;

            GameMap map;
            lock (_mapsLock)
            {
                map = Maps.FirstOrDefault(x => x.Clients.Exists(c => c.TamerId == client.TamerId));
            }

            map?.RemoveClient(client);

            var party = _partyManager.FindParty(client.TamerId);

            lock (_mapsLock)
            {
                if (party != null)
                {
                    if (map != null && map.Clients.Count == 0)
                    {
                        CleanMap(party.Id);
                        CleanMap((int)party.LeaderId);
                    }
                }
                else
                {
                    if (map != null && map.Clients.Count == 0)
                        CleanMap((int)client.TamerId);
                }
            }
        }

        public void BroadcastForChannel(byte channel, byte[] packet)
        {
            List<GameMap> maps;
            lock (_mapsLock) { maps = Maps.Where(x => x.Channel == channel).ToList(); }
            maps.ForEach(map => map.BroadcastForMap(packet));
        }

        public void BroadcastGlobal(byte[] packet)
        {
            List<GameMap> maps;
            lock (_mapsLock) { maps = Maps.Where(x => x.Clients.Any()).ToList(); }
            maps.ForEach(map => map.BroadcastForMap(packet));
        }

        public void BroadcastForMap(short mapId, byte[] packet, long tamerId)
        {
            GameMap map;
            lock (_mapsLock)
            {
                map = Maps.FirstOrDefault(x => x.MapId == mapId && x.Clients.Exists(c => c.TamerId == tamerId));
            }
            map?.BroadcastForMap(packet);
        }

        public void BroadcastForUniqueTamer(long tamerId, byte[] packet)
        {
            GameMap map;
            lock (_mapsLock)
            {
                map = Maps.FirstOrDefault(x => x.Clients.Exists(c => c.TamerId == tamerId));
            }
            map?.BroadcastForUniqueTamer(tamerId, packet);
        }

        public GameClient? FindClientByTamerId(long tamerId)
        {
            List<GameMap> maps;
            lock (_mapsLock) { maps = Maps.ToList(); }
            return maps.SelectMany(m => m.Clients).FirstOrDefault(c => c.TamerId == tamerId);
        }

        public GameClient? FindClientByTamerLogin(uint tamerLogin)
        {
            List<GameMap> maps;
            lock (_mapsLock) { maps = Maps.ToList(); }
            return maps.SelectMany(m => m.Clients).FirstOrDefault(c => c.AccountId == tamerLogin);
        }

        public GameClient? FindClientByTamerName(string tamerName)
        {
            List<GameMap> maps;
            lock (_mapsLock) { maps = Maps.ToList(); }
            return maps.SelectMany(m => m.Clients).FirstOrDefault(c => c.Tamer?.Name == tamerName);
        }

        public GameClient? FindClientByTamerHandle(int handle)
        {
            List<GameMap> maps;
            lock (_mapsLock) { maps = Maps.ToList(); }
            return maps.SelectMany(m => m.Clients).FirstOrDefault(c => c.Tamer?.GeneralHandler == handle);
        }

        public GameClient? FindClientByTamerHandleAndChannel(int handle, long tamerId)
        {
            List<GameMap> maps;
            lock (_mapsLock) { maps = Maps.Where(x => x.Clients.Exists(c => c.TamerId == tamerId)).ToList(); }
            return maps.SelectMany(m => m.Clients).FirstOrDefault(c => c.Tamer?.GeneralHandler == handle);
        }

        public void BroadcastForTargetTamers(List<long> targetTamers, byte[] packet)
        {
            GameMap map;
            lock (_mapsLock)
            {
                map = Maps.FirstOrDefault(x => x.Clients.Exists(c => targetTamers.Contains(c.TamerId)));
            }
            map?.BroadcastForTargetTamers(targetTamers, packet);
        }

        public void BroadcastForTargetTamers(long sourceId, byte[] packet)
        {
            GameMap map;
            lock (_mapsLock)
            {
                map = Maps.FirstOrDefault(x => x.Clients.Exists(c => c.TamerId == sourceId));
            }
            map?.BroadcastForTargetTamers(map.TamersView[sourceId], packet);
        }

        public void BroadcastForTamerViewsAndSelf(long sourceId, byte[] packet)
        {
            GameMap map;
            lock (_mapsLock)
            {
                map = Maps.FirstOrDefault(x => x.Clients.Exists(c => c.TamerId == sourceId));
            }
            map?.BroadcastForTamerViewsAndSelf(sourceId, packet);
        }

        public void AddMapDrop(Drop drop, long tamerId)
        {
            GameMap map;
            lock (_mapsLock)
            {
                map = Maps.FirstOrDefault(x => x.Clients.Exists(c => c.TamerId == tamerId));
            }
            map?.DropsToAdd.Add(drop);
        }

        public void RemoveDrop(Drop drop, long tamerId)
        {
            GameMap map;
            lock (_mapsLock)
            {
                map = Maps.FirstOrDefault(x => x.Clients.Exists(c => c.TamerId == tamerId));
            }
            map?.RemoveMapDrop(drop);
        }

        public Drop? GetDrop(short mapId, int dropHandler, long tamerId)
        {
            GameMap map;
            lock (_mapsLock)
            {
                map = Maps.FirstOrDefault(x => x.Clients.Exists(c => c.TamerId == tamerId));
            }
            return map?.GetDrop(dropHandler);
        }

        // ============ Mobs helpers (delegam para o GameMap) ============
        public bool MobsAttacking(short mapId, long tamerId)
        {
            GameMap map;
            lock (_mapsLock) { map = Maps.FirstOrDefault(x => x.Clients.Exists(c => c.TamerId == tamerId)); }
            return map?.MobsAttacking(tamerId) ?? false;
        }

        public bool MobsAttacking(short mapId, long tamerId, bool Summon)
        {
            GameMap map;
            lock (_mapsLock) { map = Maps.FirstOrDefault(x => x.Clients.Exists(c => c.TamerId == tamerId)); }
            return map?.MobsAttacking(tamerId) ?? false;
        }

        public List<CharacterModel> GetNearbyTamers(short mapId, long tamerId)
        {
            GameMap map;
            lock (_mapsLock) { map = Maps.FirstOrDefault(x => x.Clients.Exists(c => c.TamerId == tamerId)); }
            return map?.NearbyTamers(tamerId);
        }

        public void AddSummonMobs(short mapId, SummonMobModel summon, long tamerId)
        {
            GameMap map;
            lock (_mapsLock) { map = Maps.FirstOrDefault(x => x.Clients.Exists(c => c.TamerId == tamerId)); }
            map?.AddMob(summon);
        }

        public void AddMobs(short mapId, MobConfigModel mob, long tamerId)
        {
            GameMap map;
            lock (_mapsLock) { map = Maps.FirstOrDefault(x => x.Clients.Exists(c => c.TamerId == tamerId)); }
            map?.AddMob(mob);
        }

        public MobConfigModel? GetMobByHandler(short mapId, int handler, long tamerId)
        {
            GameMap map;
            lock (_mapsLock) { map = Maps.FirstOrDefault(x => x.Clients.Exists(c => c.TamerId == tamerId)); }
            if (map == null) return null;

            return map.Mobs.FirstOrDefault(x => x.GeneralHandler == handler);
        }

        public SummonMobModel? GetMobByHandler(short mapId, int handler, bool summon, long tamerId)
        {
            GameMap map;
            lock (_mapsLock) { map = Maps.FirstOrDefault(x => x.Clients.Exists(c => c.TamerId == tamerId)); }
            if (map == null) return null;

            return map.SummonMobs.FirstOrDefault(x => x.GeneralHandler == handler);
        }

        public List<MobConfigModel> GetMobsNearbyPartner(Location location, int range, long tamerId)
        {
            GameMap targetMap;
            lock (_mapsLock) { targetMap = Maps.FirstOrDefault(x => x.Clients.Exists(c => c.TamerId == tamerId)); }
            if (targetMap == null) return default;

            var originX = location.X;
            var originY = location.Y;

            return GetTargetMobs(targetMap.Mobs.Where(x => x.Alive).ToList(), originX, originY, range)
                   .DistinctBy(x => x.Id).ToList();
        }

        public List<MobConfigModel> GetMobsNearbyTargetMob(short mapId, int handler, int range, long tamerId)
        {
            GameMap targetMap;
            lock (_mapsLock) { targetMap = Maps.FirstOrDefault(x => x.Clients.Exists(c => c.TamerId == tamerId)); }
            if (targetMap == null) return default;

            var originMob = targetMap.Mobs.FirstOrDefault(x => x.GeneralHandler == handler);
            if (originMob == null) return default;

            var originX = originMob.CurrentLocation.X;
            var originY = originMob.CurrentLocation.Y;

            var targetMobs = new List<MobConfigModel> { originMob };
            targetMobs.AddRange(GetTargetMobs(targetMap.Mobs.Where(x => x.Alive).ToList(), originX, originY, range));

            return targetMobs.DistinctBy(x => x.Id).ToList();
        }

        public static List<MobConfigModel> GetTargetMobs(List<MobConfigModel> mobs, int originX, int originY, int range)
        {
            var targetMobs = new List<MobConfigModel>();

            foreach (var mob in mobs)
            {
                var mobX = mob.CurrentLocation.X;
                var mobY = mob.CurrentLocation.Y;

                var distance = CalculateDistance(originX, originY, mobX, mobY);
                if (distance <= range)
                    targetMobs.Add(mob);
            }

            return targetMobs;
        }

        public List<SummonMobModel> GetMobsNearbyPartner(Location location, int range, bool summon, long tamerId)
        {
            GameMap targetMap;
            lock (_mapsLock) { targetMap = Maps.FirstOrDefault(x => x.Clients.Exists(c => c.TamerId == tamerId)); }
            if (targetMap == null) return default;

            var originX = location.X;
            var originY = location.Y;

            return GetTargetMobs(targetMap.SummonMobs.Where(x => x.Alive).ToList(), originX, originY, range)
                   .DistinctBy(x => x.Id).ToList();
        }

        public List<SummonMobModel> GetMobsNearbyTargetMob(short mapId, int handler, int range, bool summon, long tamerId)
        {
            GameMap targetMap;
            lock (_mapsLock) { targetMap = Maps.FirstOrDefault(x => x.Clients.Exists(c => c.TamerId == tamerId)); }
            if (targetMap == null) return default;

            var originMob = targetMap.SummonMobs.FirstOrDefault(x => x.GeneralHandler == handler);
            if (originMob == null) return default;

            var originX = originMob.CurrentLocation.X;
            var originY = originMob.CurrentLocation.Y;

            var targetMobs = new List<SummonMobModel> { originMob };
            targetMobs.AddRange(GetTargetMobs(targetMap.SummonMobs.Where(x => x.Alive).ToList(), originX, originY, range));

            return targetMobs.DistinctBy(x => x.Id).ToList();
        }

        public static List<SummonMobModel> GetTargetMobs(List<SummonMobModel> mobs, int originX, int originY, int range)
        {
            var targetMobs = new List<SummonMobModel>();

            foreach (var mob in mobs)
            {
                var mobX = mob.CurrentLocation.X;
                var mobY = mob.CurrentLocation.Y;

                var distance = CalculateDistance(originX, originY, mobX, mobY);
                if (distance <= range)
                    targetMobs.Add(mob);
            }

            return targetMobs;
        }

        private static double CalculateDistance(int x1, int y1, int x2, int y2)
        {
            var deltaX = x2 - x1;
            var deltaY = y2 - y1;
            return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        }
    }
}
