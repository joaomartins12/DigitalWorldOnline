using DigitalWorldOnline.Commons.Writers;

namespace DigitalWorldOnline.Commons.Packets.GameServer
{
    public class ClearFriendListPacket : PacketWriter
    {
        private const int PacketNumber = 1023; // confirma esse opcode no teu cliente

        public ClearFriendListPacket()
        {
            Type(PacketNumber);
            WriteByte(0);
        }
    }
}
