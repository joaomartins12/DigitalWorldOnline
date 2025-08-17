using DigitalWorldOnline.Commons.Writers;

namespace DigitalWorldOnline.Commons.Packets.Chat
{
    public class GuildMessagePacket : PacketWriter
    {
        private const int PacketNumber = 2114;
        private const int MaxMessageLength = 256; // ajusta se o teu cliente suportar mais/menos

        public GuildMessagePacket(string senderName, string message)
        {
            senderName ??= string.Empty;
            message ??= string.Empty;

            senderName = senderName.Trim();
            message = message.Trim();

            if (message.Length > MaxMessageLength)
                message = message.Substring(0, MaxMessageLength);

            Type(PacketNumber);
            WriteString(senderName);
            WriteString(message);
        }
    }
}
