using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Writers;
using DigitalWorldOnline.GameHost;
using MediatR;
using Newtonsoft.Json;
using Serilog;
using System.Net.Sockets;
using System.Reflection;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class ArchiveAcademyInsertPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.ArchiveAcademyInsert;

        private readonly ILogger _logger;

        public ArchiveAcademyInsertPacketProcessor(
            ILogger logger)
        {
            _logger = logger;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {

            var packets = new GamePacketReader(packetData);
            var AcademySlot = packets.ReadByte();
            var ArchiveSlot = packets.ReadUInt() - 1000;
            var InventorySlot = packets.ReadUInt();

            var packet = new PacketWriter();

            packet.Type(3227);
            packet.WriteByte(AcademySlot);
            packet.WriteUInt(ArchiveSlot + 1000);
            packet.WriteInt(11);
            packet.WriteInt(1000);

            // int incubatorslotidx;
            //u4 archiveslotidx;
            //u4 itemtype;
            //u4 remaintime;
            client.Send(packet.Serialize());
            //client.Send(new ArchiveAcademyIniciarPacket());
        }

    }
}

