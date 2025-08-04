using DigitalWorldOnline.Commons.Writers;
using System.Net.Sockets;

namespace DigitalWorldOnline.Commons.Packets.GameServer
{
    public class ArchiveAcademyIniciarPacket : PacketWriter
    {
        private const int PacketNumber = 3226;

        /// <summary>
        /// Load the Cash Shop
        /// </summary>
        /// <param name="remainingSeconds">The membership remaining seconds (UTC).</param>
        public ArchiveAcademyIniciarPacket()
        {
            Type(PacketNumber);
            WriteUInt(1001);
            WriteUInt(1002);
            WriteUInt(1003);
            WriteUInt(1004);
            WriteUInt(1005);
            WriteInt(31029);
            WriteShort(1);
            WriteShort(7);
            WriteInt(0);
            WriteInt(0);
            WriteInt(0);
            WriteInt(0);
            WriteShort(8242);
            WriteByte(0);
        }
    }
}