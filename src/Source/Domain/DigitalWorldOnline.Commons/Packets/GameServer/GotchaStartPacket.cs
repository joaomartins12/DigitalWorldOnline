using DigitalWorldOnline.Commons.DTOs.Assets;
using DigitalWorldOnline.Commons.Writers;

namespace DigitalWorldOnline.Commons.Packets.GameServer
{
    public class GotchaStartPacket : PacketWriter
    {
        private const int PacketNumber = 3956;

        /// <summary>
        /// Load the Cash Shop
        /// </summary>
        /// <param name="remainingSeconds">The membership remaining seconds (UTC).</param>
        public GotchaStartPacket(GotchaAssetModel Gotcha)
        {
            Type(PacketNumber);
            WriteInt(Gotcha.RareItems.Count);

            //pop(recv.nRareItemCount);
            foreach (var rareItem in Gotcha.RareItems)
            {
                WriteUInt((uint)rareItem.RareItem);
                WriteUInt((uint)rareItem.RareItemCnt);
            }
            int totalQuanty = Gotcha.Items.Sum(item => item.Quanty);
            WriteInt(totalQuanty);
            WriteInt(100);
        }
    }
}