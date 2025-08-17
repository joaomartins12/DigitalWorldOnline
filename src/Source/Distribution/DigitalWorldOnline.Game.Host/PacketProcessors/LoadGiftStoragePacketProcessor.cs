using DigitalWorldOnline.Application;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Packets.GameServer.Combat;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class LoadGiftStoragePacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.LoadGiftStorage;

        private readonly ILogger _logger;

        public LoadGiftStoragePacketProcessor(ILogger logger)
        {
            _logger = logger;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var _ = new GamePacketReader(packetData);

            var giftStorage = client.Tamer.GiftWarehouse;

            // 🔧 NORMALIZA timers dos itens temporários para evitar “6184 days”
            NormalizeGiftTimers(giftStorage);

            client.Send(new LoadGiftStoragePacket(giftStorage));
        }

        private static void NormalizeGiftTimers(ItemListModel giftStorage)
        {
            if (giftStorage?.Items == null) return;

            var now = DateTime.UtcNow;

            foreach (var it in giftStorage.Items.Where(i => i != null && i.ItemId > 0 && i.IsTemporary))
            {
                uint minutesLeft = 0;

                // tenta usar ExpireAtUtc se existir e for futuro
                var expireProp = it.GetType().GetProperty("ExpireAtUtc");
                if (expireProp != null)
                {
                    var val = expireProp.GetValue(it);

                    DateTime? exp = null;
                    if (val is DateTime d)      // DateTime
                        exp = d;
                    else
                        exp = val as DateTime?; // DateTime?

                    if (exp.HasValue && exp.Value > now)
                    {
                        minutesLeft = (uint)Math.Max(0, Math.Round((exp.Value - now).TotalMinutes));
                    }
                }

                // fallback: usa o tempo de uso do item (minutos) se ainda não definimos
                if (minutesLeft == 0 && it.ItemInfo?.UsageTimeMinutes > 0)
                    minutesLeft = (uint)it.ItemInfo.UsageTimeMinutes;

                it.SetRemainingTime(minutesLeft);
            }
        }
    }
}
