using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.Items;
using MediatR;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class EvolutionRideUnlockPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.EvolutionRideUnlock;

        private readonly ISender _sender;
        private readonly ILogger _logger;

        public EvolutionRideUnlockPacketProcessor(ISender sender, ILogger logger)
        {
            _sender = sender;
            _logger = logger;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);

            // Vem 1-based do cliente, normalizar:
            var evoIdx = packet.ReadInt() - 1;

            // Item "section" exigido (vem do cliente)
            var itemSection = packet.ReadInt();

            // 1) Valida índice de evolução
            if (evoIdx < 0 || evoIdx >= client.Partner.Evolutions.Count)
            {
                _logger.Warning("[RideUnlock] Invalid evolution index {EvoIdx} for tamer {TamerId}.", evoIdx, client.TamerId);
                client.Send(new SystemMessagePacket("Invalid evolution index."));
                return;
            }

            var evolution = client.Partner.Evolutions[evoIdx];

            // 2) Busca o item pelo Section
            var inventoryItem = client.Tamer.Inventory.FindItemBySection(itemSection);
            if (inventoryItem == null || inventoryItem.Amount < 1)
            {
                _logger.Warning("[RideUnlock] Required item (section {Section}) not found for tamer {TamerId}.", itemSection, client.TamerId);
                client.Send(new SystemMessagePacket("Required item not found."));
                return;
            }

            // 3) Consome o item
            client.Tamer.Inventory.RemoveOrReduceItem(inventoryItem, 1);
            await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));

            // 4) Desbloqueia o Ride (idempotente: caso já esteja, método não deve quebrar)
            evolution.UnlockRide();
            await _sender.Send(new UpdateEvolutionCommand(evolution));

            // 5) Atualiza UI
            client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));
            client.Send(new SystemMessagePacket($"{evolution.Type} Ride mode unlocked!"));

            _logger.Verbose(
                "[RideUnlock] Character {TamerId} unlocked {EvolutionType} ride mode for partner {PartnerId} using section {Section}.",
                client.TamerId, evolution.Type, client.Partner.Id, itemSection
            );
        }
    }
}
