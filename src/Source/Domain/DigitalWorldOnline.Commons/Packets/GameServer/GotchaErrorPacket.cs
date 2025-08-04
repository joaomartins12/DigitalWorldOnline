using DigitalWorldOnline.Commons.DTOs.Assets;
using DigitalWorldOnline.Commons.Writers;

namespace DigitalWorldOnline.Commons.Packets.GameServer
{
    public class GotchaErrorPacket : PacketWriter
    {
        private const int PacketNumber = 3959;

        /// <summary>
        /// Load the Cash Shop
        /// </summary>
        /// <param name="remainingSeconds">The membership remaining seconds (UTC).</param>
        public GotchaErrorPacket()
        {
            Type(PacketNumber);
            WriteUInt(1);
        }
    }
}