using DigitalWorldOnline.Application;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Packets.GameServer;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class RareMachineOpenPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.RareMachineOpen;
        private readonly AssetsLoader _assets;


        public RareMachineOpenPacketProcessor(AssetsLoader assets)
        {
            _assets = assets;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);
            var NpcId = packet.ReadInt();
            var Gotcha = _assets.Gotcha.FirstOrDefault(x => x.NpcId == NpcId);
            client.Send(new GotchaStartPacket(Gotcha));
        }
    }
}
