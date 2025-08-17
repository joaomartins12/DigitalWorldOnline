using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Packets.GameServer;
using System.Threading.Tasks;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class FriendInformationPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.FriendInformation;

        public FriendInformationPacketProcessor()
        {
        }

        public Task Process(GameClient client, byte[] packetData)
        {
            // Proteções básicas
            if (client == null || client.Tamer == null)
                return Task.CompletedTask;

            // Se no futuro o pacote trouxer filtros/offsets, podes ler aqui:
            // var packet = new GamePacketReader(packetData);

            // Envia o snapshot atual (forma segura; evita exceções em cadeia)
            try
            {
                client.Send(new FriendInformationPacket());
            }
            catch
            {
                // Sem logger neste processor; não propagar erro para não quebrar o loop
            }

            return Task.CompletedTask;
        }
    }
}
