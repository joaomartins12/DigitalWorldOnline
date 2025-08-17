using DigitalWorldOnline.Application;
using DigitalWorldOnline.Application.Separar.Commands.Create;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Asset;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Models.Digimon;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Packets.Items;
using DigitalWorldOnline.Commons.Utils;
using DigitalWorldOnline.GameHost;
using DigitalWorldOnline.Game.Managers;
using MediatR;
using Serilog;

namespace DigitalWorldOnline.Game.PacketProcessors
{
    public class HatchSpiritEvolutionPacketProcessor : IGamePacketProcessor
    {
        public GameServerPacketEnum Type => GameServerPacketEnum.HatchSpiritEvolution;

        private readonly StatusManager _statusManager;
        private readonly MapServer _mapServer;
        private readonly DungeonsServer _dungeonServer;
        private readonly AssetsLoader _assets;
        private readonly ILogger _logger;
        private readonly ISender _sender;

        public HatchSpiritEvolutionPacketProcessor(
            StatusManager statusManager,
            MapServer mapServer,
            AssetsLoader assets,
            ILogger logger,
            ISender sender,
            DungeonsServer dungeonsServer
        )
        {
            _statusManager = statusManager;
            _mapServer = mapServer;
            _assets = assets;
            _logger = logger;
            _sender = sender;
            _dungeonServer = dungeonsServer;
        }

        public async Task Process(GameClient client, byte[] packetData)
        {
            var packet = new GamePacketReader(packetData);

            var targetType = packet.ReadInt();     // Digimon base type a nascer (spirit)
            var digiName = packet.ReadString();  // nome escolhido
            _ = packet.ReadByte();                 // byte sem uso no protocolo original
            var npcId = packet.ReadInt();     // NPC que executa a evolução extra

            // 1) Npc e configuração
            var extraEvolutionNpc = _assets.ExtraEvolutions.FirstOrDefault(x => x.NpcId == npcId);
            if (extraEvolutionNpc == null)
            {
                _logger.Warning("[HatchSpirit] ExtraEvolution NPC {NpcId} não encontrado.", npcId);
                client.Send(new SystemMessagePacket("NPC inválido para Spirit Evolution."));
                return;
            }

            var extraEvolutionInfo = extraEvolutionNpc
                .ExtraEvolutionInformation
                .FirstOrDefault(info => info.ExtraEvolution.Any(e => e.DigimonId == targetType))
                ?.ExtraEvolution;

            if (extraEvolutionInfo == null)
            {
                _logger.Warning("[HatchSpirit] Nenhuma configuração ExtraEvolution para o DigimonId {Type} no NPC {NpcId}.", targetType, npcId);
                client.Send(new SystemMessagePacket("Configuração de Spirit Evolution não encontrada."));
                return;
            }

            var extraEvolution = extraEvolutionInfo.FirstOrDefault(x => x.DigimonId == targetType);
            if (extraEvolution == null)
            {
                _logger.Warning("[HatchSpirit] ExtraEvolution nula para Type={Type} no NPC {NpcId}.", targetType, npcId);
                client.Send(new SystemMessagePacket("Configuração de Spirit Evolution inválida."));
                return;
            }

            // 2) Verifica slot livre
            byte freeSlot = 0;
            while (freeSlot < client.Tamer.DigimonSlots && client.Tamer.Digimons.Any(d => d.Slot == freeSlot))
                freeSlot++;

            if (freeSlot >= client.Tamer.DigimonSlots)
            {
                client.Send(new SystemMessagePacket("Sem espaço nos slots de Digimon."));
                return;
            }

            // 3) Verifica recursos (bits + materiais + requireds) ANTES de consumir
            if (client.Tamer.Inventory.Bits < extraEvolution.Price)
            {
                client.Send(new SystemMessagePacket("Bits insuficientes."));
                _logger.Warning("[HatchSpirit] Bits insuficientes. Precisa {Price}, tem {Bits}.",
                    extraEvolution.Price, client.Tamer.Inventory.Bits);
                return;
            }

            // Material: exige 1 dos listados (no teu fluxo original remove o primeiro que tiver)
            ExtraEvolutionMaterialAssetModel? chosenMaterial = null;
            foreach (var mat in extraEvolution.Materials)
            {
                var item = client.Tamer.Inventory.FindItemById(mat.ItemId);
                if (item != null && item.Amount >= mat.Amount)
                {
                    chosenMaterial = mat;
                    break;
                }
            }
            if (extraEvolution.Materials.Any() && chosenMaterial == null)
            {
                client.Send(new SystemMessagePacket("Materiais insuficientes para esta Spirit Evolution."));
                _logger.Warning("[HatchSpirit] Falta material obrigatório. Nenhum dos itens {Ids} disponível.",
                    string.Join(", ", extraEvolution.Materials.Select(m => $"{m.ItemId}x{m.Amount}")));
                return;
            }

            // Requireds: pode ter de 1 a N exigências. No teu código original remove até 3; aqui valida todos listados
            var requiredsSatisfied = new List<ExtraEvolutionRequiredAssetModel>();
            foreach (var req in extraEvolution.Requireds)
            {
                var item = client.Tamer.Inventory.FindItemById(req.ItemId);
                if (item == null || item.Amount < req.Amount)
                {
                    client.Send(new SystemMessagePacket("Itens obrigatórios insuficientes para esta Spirit Evolution."));
                    _logger.Warning("[HatchSpirit] Requisito ausente: ItemId {ItemId} x{Amount}.", req.ItemId, req.Amount);
                    return;
                }
                requiredsSatisfied.Add(req);
            }

            // 4) Consome recursos (agora com segurança)
            client.Tamer.Inventory.RemoveBits(extraEvolution.Price);

            var materialsUsedForPacket = new List<ExtraEvolutionMaterialAssetModel>();
            var requiredsUsedForPacket = new List<ExtraEvolutionRequiredAssetModel>();

            if (chosenMaterial != null)
            {
                client.Tamer.Inventory.RemoveOrReduceItemWithoutSlot(new ItemModel(chosenMaterial.ItemId, chosenMaterial.Amount));
                materialsUsedForPacket.Add(chosenMaterial);
            }

            foreach (var req in requiredsSatisfied)
            {
                client.Tamer.Inventory.RemoveOrReduceItemWithoutSlot(new ItemModel(req.ItemId, req.Amount));
                requiredsUsedForPacket.Add(req);
            }

            // 5) Cria o Digimon
            var newDigimon = DigimonModel.Create(
                digiName,
                targetType,
                targetType,
                DigimonHatchGradeEnum.Default,
                UtilitiesFunctions.GetLevelSize(3),
                freeSlot
            );

            newDigimon.NewLocation(client.Tamer.Location.MapId, client.Tamer.Location.X, client.Tamer.Location.Y);
            newDigimon.SetBaseInfo(_statusManager.GetDigimonBaseInfo(newDigimon.BaseType));
            newDigimon.SetBaseStatus(_statusManager.GetDigimonBaseStatus(newDigimon.BaseType, newDigimon.Level, newDigimon.Size));
            newDigimon.AddEvolutions(_assets.EvolutionInfo.First(x => x.Type == newDigimon.BaseType));

            if (newDigimon.BaseInfo == null || newDigimon.BaseStatus == null || !newDigimon.Evolutions.Any())
            {
                _logger.Warning("[HatchSpirit] Informações do Digimon {Type} incompletas (base/basestatus/evolutions).", newDigimon.BaseType);
                client.Send(new SystemMessagePacket($"Informações do Digimon {newDigimon.BaseType} indisponíveis."));
                return;
            }

            newDigimon.SetTamer(client.Tamer);
            client.Tamer.AddDigimon(newDigimon);

            // 6) Mensagens de “perfeito”
            if (client.Tamer.Incubator.PerfectSize(newDigimon.HatchGrade, newDigimon.Size))
            {
                var neon = new NeonMessagePacket(NeonMessageTypeEnum.Scale, client.Tamer.Name, newDigimon.BaseType, newDigimon.Size).Serialize();
                _mapServer.BroadcastGlobal(neon);
                _dungeonServer.BroadcastGlobal(neon);
            }

            // 7) Persistência
            var digimonInfo = await _sender.Send(new CreateDigimonCommand(newDigimon));
            if (digimonInfo != null)
            {
                newDigimon.SetId(digimonInfo.Id);

                // amarra IDs das evoluções/skills criadas no banco
                var idx = -1;
                foreach (var evo in newDigimon.Evolutions)
                {
                    idx++;
                    var dto = digimonInfo.Evolutions[idx];
                    if (dto == null) continue;

                    evo.SetId(dto.Id);

                    var sIdx = -1;
                    foreach (var sk in evo.Skills)
                    {
                        sIdx++;
                        var dtoSkill = dto.Skills[sIdx];
                        sk.SetId(dtoSkill.Id);
                    }
                }
            }

            await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
            await _sender.Send(new UpdateItemListBitsCommand(client.Tamer.Inventory.Id, client.Tamer.Inventory.Bits));

            // 8) Pacotes de retorno
            client.Send(new HatchFinishPacket(newDigimon, (ushort)(client.Partner.GeneralHandler + 1000),
                client.Tamer.Digimons.FindIndex(x => x == newDigimon)));

            client.Send(new HatchSpiritEvolutionPacket(
                targetType,
                (int)client.Tamer.Inventory.Bits,
                materialsUsedForPacket,
                requiredsUsedForPacket
            ));

            client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));

            _logger.Verbose("[HatchSpirit] Tamer {TamerId} hatchou spirit {DigiId} ({Type}) grade {Grade} size {Size}.",
                client.TamerId, newDigimon.Id, newDigimon.BaseType, newDigimon.HatchGrade, newDigimon.Size);
        }
    }
}
