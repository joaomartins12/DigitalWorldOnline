using System.Linq;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Writers;

namespace DigitalWorldOnline.Commons.Packets.GameServer.Combat
{
    public class LoadGiftStoragePacket : PacketWriter
    {
        private const int PacketNumber = 3935;

        /// <summary>
        /// Envia para o cliente a lista atualizada do Gift Storage.
        /// Mantém o tamanho original mas calcula contagem real de itens válidos.
        /// </summary>
        public LoadGiftStoragePacket(ItemListModel giftStorage)
        {
            Type(PacketNumber);

            if (giftStorage == null || giftStorage.Items == null)
            {
                WriteShort(0);
                return;
            }

            // ✅ Conta apenas os itens ocupados (ItemId > 0) mas não remove slots vazios
            short validCount = (short)giftStorage.Items.Count(i => i != null && i.ItemId > 0);
            WriteShort(validCount);

            // ✅ Atualiza informações de tempo restante para itens temporários
            foreach (var it in giftStorage.Items.Where(i => i != null && i.ItemId > 0 && i.IsTemporary))
            {
                uint minutesLeft = 0;

                // Usa ExpireAtUtc se existir
                var expireProp = it.GetType().GetProperty("ExpireAtUtc");
                if (expireProp != null)
                {
                    var val = expireProp.GetValue(it);
                    if (val is DateTime dt)
                    {
                        if (dt > DateTime.UtcNow)
                        {
                            minutesLeft = (uint)Math.Max(0, (dt - DateTime.UtcNow).TotalMinutes);
                        }
                    }
                    else if (val is DateTime? && ((DateTime?)val).HasValue && ((DateTime?)val).Value > DateTime.UtcNow)
                    {
                        var ndt = (DateTime?)val;
                        minutesLeft = (uint)Math.Max(0, (ndt.Value - DateTime.UtcNow).TotalMinutes);
                    }
                }

                // Se não tem ExpireAtUtc ou é inválido, usa UsageTimeMinutes como fallback
                if (minutesLeft == 0 && it.ItemInfo?.UsageTimeMinutes > 0)
                {
                    minutesLeft = (uint)it.ItemInfo.UsageTimeMinutes;

                    // Opcional: setar ExpireAtUtc para consistência
                    if (expireProp != null && expireProp.CanWrite)
                    {
                        var newExpire = DateTime.UtcNow.AddMinutes(minutesLeft);
                        if (expireProp.PropertyType == typeof(DateTime))
                            expireProp.SetValue(it, newExpire);
                        else if (expireProp.PropertyType == typeof(DateTime?))
                            expireProp.SetValue(it, (DateTime?)newExpire);
                    }
                }

                it.SetRemainingTime(minutesLeft);
            }

            // ✅ Serializa todos os slots no formato esperado pelo cliente
            WriteBytes(giftStorage.NewGiftToArray());
        }
    }
}
