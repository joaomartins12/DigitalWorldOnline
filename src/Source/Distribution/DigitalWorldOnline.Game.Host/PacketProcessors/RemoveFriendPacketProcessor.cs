using System.Linq;
using System.Threading.Tasks;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Packets.GameServer;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class RemoveFriendPacketProcessor : IGamePacketProcessor
    {
        // Mapeia 2402 -> ajuste o enum se ainda não existir
        public GameServerPacketEnum Type => GameServerPacketEnum.RemoveFriend;

        public async Task Process(GameClient client, byte[] packetData)
        {
            var reader = new GamePacketReader(packetData);

            // Pelo teu cliente, 2402 envia o NOME do amigo a remover
            var friendName = reader.ReadString();

            var friends = client.Tamer?.Friends;
            if (friends == null || friends.Count == 0)
                return;

            var toRemove = friends.FirstOrDefault(f =>
                string.Equals(f.Name, friendName, System.StringComparison.OrdinalIgnoreCase));

            if (toRemove == null)
            {
                // ✅ precisa do nome no construtor
                client.Send(new FriendNotFoundPacket(friendName));
                return;
            }

            // Remove da lista em memória
            friends.Remove(toRemove);

            // TODO (opcional): persistir no storage se tiveres um comando para isso
            // await _sender.Send(new RemoveFriendCommand(client.TamerId, toRemove.FriendId));

            // Atualiza a UI do cliente (o cliente geralmente remove localmente, mas garantimos estado)
            client.Send(new FriendDisconnectPacket(friendName));
        }
    }
}
