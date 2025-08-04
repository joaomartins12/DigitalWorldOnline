using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.GameHost;
using MediatR;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class DigimonChangeNamePacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.DigimonChangeName;

        private readonly MapServer _mapServer;
        private readonly ILogger _logger;
        private readonly ISender _sender;

        public DigimonChangeNamePacketProcessor(
            MapServer mapServer,
            ILogger logger,
            ISender sender)
        {
            _mapServer = mapServer;
            _logger = logger;
            _sender = sender;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);

            int itemSlot = packet.ReadInt();
            var newName = packet.ReadString();
            var oldName = client.Tamer.Partner.Name;
            var digimonID = client.Tamer.Partner.Id;

            var inventoryItem = client.Tamer.Inventory.FindItemBySlot(itemSlot);

            if (inventoryItem != null)
            {
                client.Tamer.Inventory.RemoveOrReduceItem(inventoryItem, 1, itemSlot);
                client.Tamer.Partner.UpdateDigimonName(newName);

                await _sender.Send(new ChangeDigimonNameByIdCommand(digimonID, newName));
                await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));

                //client.Send(new DigimonChangeNamePacket(CharacterChangeNameType.Sucess, itemSlot, oldName, newName));
                //client.Send(new DigimonChangeNamePacket(CharacterChangeNameType.Complete, oldName, newName, itemSlot));

                _logger.Verbose($"Character {client.TamerId} Changed Digimon Name {oldName} to {newName}.");
            }
            else
            {
                _logger.Error($"Item nao encontrado !!");
            }

        }
    }
}