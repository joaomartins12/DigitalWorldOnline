using DigitalWorldOnline.Commons.Models.Map;
using DigitalWorldOnline.Commons.Packets.MapServer;
using System.Diagnostics;

namespace DigitalWorldOnline.GameHost
{
    public sealed partial class DungeonsServer
    {
        private void DropsOperation(GameMap map)
        {
            // Se não há jogadores conectados, não há nada para fazer
            if (!map.ConnectedTamers.Any())
                return;

            var sw = new Stopwatch();
            sw.Start();

            // 1) Aplicar pendências de adição (thread-safe)
            lock (map.DropsLock)
            {
                if (map.DropsToAdd.Count > 0)
                {
                    foreach (var drop in map.DropsToAdd)
                        map.AddDrop(drop);

                    map.DropsToAdd.Clear();
                }
            }

            // 2) Snapshot da lista de drops para iterar sem segurar lock
            List<Drop> snapshot;
            lock (map.DropsLock)
            {
                snapshot = map.Drops.ToList();
            }
            var snapshotCount = snapshot.Count;

            // 3) Processar cada drop (mostrar/ocultar e expiração)
            foreach (var drop in snapshot)
            {
                var nearTamers = map.NearestTamers(drop);
                var farTamers = map.FarawayTamers(drop);

                ShowAndHideDrop(map, drop, nearTamers, farTamers);
                CheckExpiredDrop(map, drop);
                // Se usar "lost drop" (sem dono após período), habilite:
                // CheckLostDrop(map, drop);
            }

            // 4) Aplicar pendências de remoção (thread-safe)
            lock (map.DropsLock)
            {
                if (map.DropsToRemove.Count > 0)
                {
                    foreach (var drop in map.DropsToRemove)
                        map.RemoveDrop(drop);

                    map.DropsToRemove.Clear();
                }
            }

            // 5) Métrica
            sw.Stop();
            var totalMs = sw.Elapsed.TotalMilliseconds;
            if (totalMs >= 1000)
                Console.WriteLine($"DropsOperation ({snapshotCount}) took {totalMs:0.0}ms.");
        }

        private void ShowAndHideDrop(GameMap map, Drop drop, List<long> nearTamers, List<long> farTamers)
        {
            // mostrar para quem entrou na área
            foreach (var tamerId in nearTamers)
            {
                if (!map.ViewingDrop(drop.Id, tamerId))
                {
                    var client = map.Clients.FirstOrDefault(c => c.TamerId == tamerId);

                    map.ShowDrop(drop.Id, tamerId);
                    client?.Send(new LoadDropsPacket(drop));
                }
            }

            // ocultar de quem saiu da área
            foreach (var tamerId in farTamers)
            {
                if (map.ViewingDrop(drop.Id, tamerId) && !nearTamers.Contains(tamerId))
                {
                    var client = map.Clients.FirstOrDefault(c => c.TamerId == tamerId);

                    map.HideDrop(drop.Id, tamerId);
                    client?.Send(new UnloadDropsPacket(drop));
                }
            }
        }

        private void CheckLostDrop(GameMap map, Drop drop)
        {
            // Se o drop não tem dono e ainda não foi marcado como "perdido", reatribui visibilidade
            if (!drop.Lost && !drop.Thrown && drop.NoOwner)
            {
                var dropViews = new List<long>(map.GetDropViews(drop.Id));

                foreach (var tamerId in dropViews)
                {
                    var client = map.Clients.FirstOrDefault(x => x.TamerId == tamerId);

                    client?.Send(new UnloadDropsPacket(drop));
                    client?.Send(new LoadDropsPacket(drop, client.Tamer.GeneralHandler));
                }

                drop.SetLost();
            }
        }

        private void CheckExpiredDrop(GameMap map, Drop drop)
        {
            if (!drop.Expired)
                return;

            bool alreadyQueued;
            lock (map.DropsLock)
            {
                alreadyQueued = map.DropsToRemove.Any(x => x.Id == drop.Id);
            }
            if (alreadyQueued)
                return;

            var dropViews = new List<long>(map.GetDropViews(drop.Id));

            foreach (var tamerId in dropViews)
            {
                var client = map.Clients.FirstOrDefault(x => x.TamerId == tamerId);

                map.HideDrop(drop.Id, tamerId);
                client?.Send(new UnloadDropsPacket(drop));
            }

            lock (map.DropsLock)
            {
                map.DropsToRemove.Add(drop);
            }
        }
    }
}
