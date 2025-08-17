using DigitalWorldOnline.Commons.Writers;

namespace DigitalWorldOnline.Commons.Packets.GameServer
{
    public class RemoveFriendPacket : PacketWriter
    {
        private const int PacketNumber = 2402; // remove friend

        public RemoveFriendPacket(string name)
        {
            Type(PacketNumber);
            WriteString(name);
        }
    }
}
