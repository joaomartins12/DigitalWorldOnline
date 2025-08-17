using DigitalWorldOnline.Application;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.Items;
using MediatR;
using Serilog;
using System;
using System.Linq;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class QuestAcceptPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.QuestAccept;

        private readonly AssetsLoader _assets;
        private readonly ILogger _logger;
        private readonly ISender _sender;

        public QuestAcceptPacketProcessor(
            AssetsLoader assets,
            ILogger logger,
            ISender sender)
        {
            _assets = assets;
            _logger = logger;
            _sender = sender;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);

            // O protocolo envia como short
            short questId = packet.ReadShort();

            try
            {
                // 1) Marca como aceita no progresso (retorna false se já aceita/cheia/indisponível)
                if (!client.Tamer.Progress.AcceptQuest(questId))
                {
                    client.Send(new SystemMessagePacket($"Quest {questId} cannot be accepted right now."));
                    return;
                }

                // 2) Carrega a configuração da quest
                var questInfo = _assets.Quest.FirstOrDefault(x => x.QuestId == (int)questId);
                if (questInfo == null)
                {
                    _logger.Error("QuestAccept: unknown quest id {QuestId}.", questId);
                    client.Send(new SystemMessagePacket($"Unknown quest id {questId}."));
                    client.Tamer.Progress.RemoveQuest(questId);
                    return;
                }

                // 3) Entrega dos suprimentos da quest (se houver). Se falhar, desfaz a aceitação.
                foreach (var supply in questInfo.QuestSupplies)
                {
                    var info = _assets.ItemInfo.FirstOrDefault(x => x.ItemId == supply.ItemId);
                    if (info == null)
                    {
                        _logger.Error("QuestAccept: item info not found for item {ItemId} (quest {QuestId}).", supply.ItemId, questId);
                        client.Send(new SystemMessagePacket($"Item information not found for item {supply.ItemId}."));
                        client.Tamer.Progress.RemoveQuest(questId);
                        return;
                    }

                    var item = new ItemModel();
                    item.SetItemId(supply.ItemId);
                    item.SetAmount(supply.Amount);
                    item.SetItemInfo(info);

                    // tenta adicionar (respeita espaço no inventário)
                    var toAdd = (ItemModel)item.Clone();
                    if (!client.Tamer.Inventory.AddItem(toAdd))
                    {
                        client.Send(new PickItemFailPacket(PickItemFailReasonEnum.InventoryFull));
                        client.Tamer.Progress.RemoveQuest(questId);
                        return;
                    }
                }

                _logger.Verbose("QuestAccept: character {TamerId} accepted quest {QuestId}.", client.TamerId, questId);
                foreach (var s in questInfo.QuestSupplies)
                    _logger.Verbose("QuestAccept: character {TamerId} received supply {ItemId} x{Amount} for quest {QuestId}.",
                        client.TamerId, s.ItemId, s.Amount, questId);

                // 4) Persiste inventário e progresso
                await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
                await _sender.Send(new AddCharacterProgressCommand(client.Tamer.Progress));

                // 5) Refresh de UI
                client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));
            }
            catch (Exception ex)
            {
                // Em caso de exceção, garante que a quest não fica marcada como aceita
                _logger.Error(ex, "QuestAccept: exceção ao aceitar quest {QuestId} (tamer {TamerId}).", questId, client?.TamerId);
                client?.Tamer?.Progress?.RemoveQuest(questId);
                client?.Send(new SystemMessagePacket($"Failed to accept quest {questId}."));
            }
        }
    }
}
