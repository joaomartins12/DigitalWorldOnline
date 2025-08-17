using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Writers;

namespace DigitalWorldOnline.Commons.Packets.GameServer
{
    public class LoadRewardStoragePacket : PacketWriter
    {
        private const int PacketNumber = 16001;

        public LoadRewardStoragePacket(ItemListModel giftStorage)
        {
            Type(PacketNumber);
            WriteShort(giftStorage.Count);
            // ✅ usa o mesmo formato que o Gift usa
            WriteBytes(giftStorage.NewGiftToArray());
        }
    }
}
