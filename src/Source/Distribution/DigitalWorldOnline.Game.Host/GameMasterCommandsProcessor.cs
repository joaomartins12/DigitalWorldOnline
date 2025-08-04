using AutoMapper;
using DigitalWorldOnline.Application;
using DigitalWorldOnline.Application.Admin.Queries;
using DigitalWorldOnline.Application.Separar.Commands.Create;
using DigitalWorldOnline.Application.Separar.Commands.Delete;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Application.Separar.Queries;
using DigitalWorldOnline.Commons.DTOs.Assets;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums;
using DigitalWorldOnline.Commons.Enums.Account;
using DigitalWorldOnline.Commons.Enums.Character;
using DigitalWorldOnline.Commons.Enums.ClientEnums;
using DigitalWorldOnline.Commons.Models.Account;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Models.Character;
using DigitalWorldOnline.Commons.Models.Digimon;
using DigitalWorldOnline.Commons.Models.Summon;
using DigitalWorldOnline.Commons.Packets.Chat;
using DigitalWorldOnline.Commons.Packets.GameServer;
using DigitalWorldOnline.Commons.Packets.GameServer.Combat;
using DigitalWorldOnline.Commons.Packets.Items;
using DigitalWorldOnline.Commons.Packets.MapServer;
using DigitalWorldOnline.Commons.Utils;
using DigitalWorldOnline.Commons.Writers;
using DigitalWorldOnline.Game.Managers;
using DigitalWorldOnline.GameHost;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using System.Text.RegularExpressions;

namespace DigitalWorldOnline.Game
{
    public sealed class GameMasterCommandsProcessor : IDisposable
    {
        private const string GameServerAddress = "GameServer:Address";
        private const string GamerServerPublic = "GameServer:PublicAddress";
        private const string GameServerPort = "GameServer:Port";

        private readonly PartyManager _partyManager;
        private readonly StatusManager _statusManager;
        private readonly ExpManager _expManager;
        private readonly AssetsLoader _assets;
        private readonly MapServer _mapServer;
        private readonly DungeonsServer _dungeonServer;
        private readonly PvpServer _pvpServer;
        private readonly ILogger _logger;
        private readonly ISender _sender;
        private readonly IMapper _mapper;
        private readonly IConfiguration _configuration;

        public GameMasterCommandsProcessor(
            PartyManager partyManager,
            StatusManager statusManager,
            ExpManager expManager,
            AssetsLoader assets,
            MapServer mapServer,
            DungeonsServer dungeonsServer,
            PvpServer pvpServer,
            ILogger logger,
            ISender sender,
            IMapper mapper,
            IConfiguration configuration)
        {
            _partyManager = partyManager;
            _expManager = expManager;
            _statusManager = statusManager;
            _assets = assets;
            _mapServer = mapServer;
            _dungeonServer = dungeonsServer;
            _pvpServer = pvpServer;
            _logger = logger;
            _sender = sender;
            _mapper = mapper;
            _configuration = configuration;
        }

        public async Task ExecuteCommand(GameClient client, string message)
        {
            var command = Regex.Replace(message.Trim().ToLower(), @"\s+", " ").Split(' ');

            _logger.Information($"Account {client.AccountId} {client.Tamer.Name} used !{message}.");

            switch (command[0])
            {

                case "maintenance":
                    {
                        if (client.AccessLevel == AccountAccessLevelEnum.Administrator)
                        {
                            // Enviar mensagem de aviso inicial
                            await SendGlobalNotice("Server shutdown for maintenance in 2 minutes");

                            // Atualizar o servidor
                            var server = await _sender.Send(new GetServerByIdQuery(client.ServerId));
                            if (server.Register != null)
                            {
                                await _sender.Send(new UpdateServerCommand(server.Register.Id, server.Register.Name, server.Register.Experience, true));
                            }

                            // Iniciar tarefa de contagem regressiva para manutenção
                            await RunMaintenanceCountdownAsync();

                        }
                    }
                    break;

                case "hatch":
                    {
                        var regex = @"^hatch";
                        var match = Regex.IsMatch(message, regex, RegexOptions.IgnoreCase);

                        if (!match)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command.\nType !hatch (Type) (Name)"));
                            break;
                        }

                        if (command.Length < 3)
                        {
                            client.Send(new SystemMessagePacket("Invalid command.\nType !hatch (Type) (Name)"));
                            break;
                        }

                        if (!int.TryParse(command[1], out int digiId))
                        {
                            client.Send(new SystemMessagePacket("Invalid digimon Id.\nType numeric value."));
                            break;
                        }

                        var digiName = command[2];

                        /*if (digiId == 31001 || digiId == 31002 || digiId == 31003 || digiId == 31004)
                        {
                            client.Send(new SystemMessagePacket($"You cant hatch starter digimon, sorry :P"));
                            break;
                        }*/

                        var newDigi = _assets.DigimonBaseInfo.First(x => x.Type == digiId);

                        if (newDigi == null)
                        {
                            client.Send(new SystemMessagePacket($"Digimon Type {digiId} not found!!"));
                            _logger.Error($"Digimon Type {digiId} not found!! [ Hatch Command ]");
                            break;
                        }
                        else if (newDigi?.EvolutionType != 3 && newDigi?.EvolutionType != 10)
                        {
                            client.Send(new SystemMessagePacket($"Digimon Type {digiId} is not a Rookie or Spirit Digimon !!"));
                            break;
                        }

                        byte i = 0;
                        while (i < client.Tamer.DigimonSlots)
                        {
                            if (client.Tamer.Digimons.FirstOrDefault(x => x.Slot == i) == null)
                                break;

                            i++;
                        }

                        var newDigimon = DigimonModel.Create(
                            digiName,
                            digiId,
                            digiId,
                            DigimonHatchGradeEnum.Perfect,
                            12500,
                            i
                        );

                        newDigimon.NewLocation(client.Tamer.Location.MapId, client.Tamer.Location.X, client.Tamer.Location.Y);

                        newDigimon.SetBaseInfo(_statusManager.GetDigimonBaseInfo(newDigimon.BaseType));
                        newDigimon.SetBaseStatus(_statusManager.GetDigimonBaseStatus(newDigimon.BaseType, newDigimon.Level, newDigimon.Size));

                        var digimonEvolutionInfo = _assets.EvolutionInfo.First(x => x.Type == newDigimon.BaseType);

                        newDigimon.AddEvolutions(digimonEvolutionInfo);

                        if (newDigimon.BaseInfo == null || newDigimon.BaseStatus == null || !newDigimon.Evolutions.Any())
                        {
                            _logger.Warning($"Unknown digimon info for {newDigimon.BaseType}.");
                            client.Send(new SystemMessagePacket($"Unknown digimon info for {newDigimon.BaseType}."));
                            return;
                        }

                        newDigimon.SetTamer(client.Tamer);

                        client.Tamer.AddDigimon(newDigimon);

                        client.Send(new HatchFinishPacket(newDigimon, (ushort)(client.Partner.GeneralHandler + 1000), client.Tamer.Digimons.FindIndex(x => x == newDigimon)));

                        var digimonInfo = await _sender.Send(new CreateDigimonCommand(newDigimon));

                        if (digimonInfo != null)
                        {
                            newDigimon.SetId(digimonInfo.Id);
                            var slot = -1;

                            foreach (var digimon in newDigimon.Evolutions)
                            {
                                slot++;

                                var evolution = digimonInfo.Evolutions[slot];

                                if (evolution != null)
                                {
                                    digimon.SetId(evolution.Id);

                                    var skillSlot = -1;

                                    foreach (var skill in digimon.Skills)
                                    {
                                        skillSlot++;

                                        var dtoSkill = evolution.Skills[skillSlot];

                                        skill.SetId(dtoSkill.Id);
                                    }
                                }
                            }
                        }

                        _mapServer.BroadcastForUniqueTamer(client.TamerId, new NeonMessagePacket(NeonMessageTypeEnum.Scale, client.Tamer.Name, newDigimon.BaseType, newDigimon.Size).Serialize());

                        // -- ADD ENCYCLOPEDIA ---------------------------------------------------------------------------------------------

                        var digimonBaseInfo = newDigimon.BaseInfo;
                        var digimonEvolutions = newDigimon.Evolutions;

                        var encyclopediaExists = client.Tamer.Encyclopedia.Exists(x => x.DigimonEvolutionId == digimonEvolutionInfo?.Id);

                        // Check if encyclopedia exists
                        if (!encyclopediaExists && digimonEvolutionInfo != null)
                        {
                            var encyclopedia = CharacterEncyclopediaModel.Create(client.TamerId, digimonEvolutionInfo.Id, newDigimon.Level, newDigimon.Size, 0, 0, 0, 0, 0, false, false);

                            digimonEvolutions?.ForEach(x =>
                            {
                                var evolutionLine = digimonEvolutionInfo.Lines.FirstOrDefault(y => y.Type == x.Type);
                                byte slotLevel = 0;

                                if (evolutionLine != null)
                                {
                                    slotLevel = evolutionLine.SlotLevel;
                                }

                                var encyclopediaEvo = CharacterEncyclopediaEvolutionsModel.Create(x.Type, slotLevel, Convert.ToBoolean(x.Unlocked));

                                encyclopedia.Evolutions.Add(encyclopediaEvo);
                            });

                            var encyclopediaAdded = await _sender.Send(new CreateCharacterEncyclopediaCommand(encyclopedia));

                            client.Tamer.Encyclopedia.Add(encyclopediaAdded);
                        }

                    }
                    break;

                case "delete":
                    {
                        var regex = @"^delete";
                        var match = Regex.IsMatch(message, regex, RegexOptions.IgnoreCase);

                        if (!match)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command.\nType !delete (slot) (email)"));
                            break;
                        }

                        if (command.Length < 3)
                        {
                            client.Send(new SystemMessagePacket("Invalid command.\nType !delete (slot) (email)"));
                            break;
                        }

                        if (!byte.TryParse(command[1], out byte digiSlot))
                        {
                            client.Send(new SystemMessagePacket("Invalid Slot.\nType a valid Slot (1 to 4)"));
                            break;
                        }

                        if (digiSlot == 0)
                        {
                            client.Send(new SystemMessagePacket($"Digimon in slot 0 cant be deleted !!"));
                            break;
                        }

                        string validation = command[2].ToLower();

                        var digimon = client.Tamer.Digimons.FirstOrDefault(x => x.Slot == digiSlot);

                        if (digimon == null)
                        {
                            client.Send(new SystemMessagePacket($"Digimon not found on slot {digiSlot}"));
                            break;
                        }

                        var digimonId = digimon.Id;

                        var result = client.PartnerDeleteValidation(validation);

                        if (result > 0)
                        {
                            client.Tamer.RemoveDigimon(digiSlot);

                            client.Send(new PartnerDeletePacket(digiSlot));

                            await _sender.Send(new DeleteDigimonCommand(digimonId));

                            _logger.Verbose($"Tamer {client.Tamer.Name} deleted partner {digimonId}.");
                        }
                        else
                        {
                            client.Send(new PartnerDeletePacket(result));
                            _logger.Verbose($"Tamer {client.Tamer.Name} failed to deleted partner {digimonId} with invalid account information.");
                        }

                    }
                    break;

                case "notice":
                    {
                        var notice = string.Join(" ", message.Split(' ').Skip(1));
                        var packet = new PacketWriter();
                        packet.Type(1006);
                        packet.WriteByte(10);
                        packet.WriteByte(1);
                        packet.WriteString($"{notice}");
                        packet.WriteByte(0);

                        _mapServer.BroadcastGlobal(packet.Serialize());
                    }
                    break;

                case "where":
                    {
                        var regex = @"(where$){1}|(location$){1}|(position$){1}|(pos$){1}";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                            break;
                        }

                        var loc = client.Tamer.Location;
                        client.Send(new SystemMessagePacket($"Map: {loc.MapId} X: {loc.X} Y: {loc.Y}"));
                    }
                    break;

                case "tamer":
                    {
                        if (command.Length == 1)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                            break;
                        }

                        switch (command[1])
                        {
                            case "size":
                                {
                                    var regex = @"(tamer\ssize\s\d){1}";
                                    var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                                    if (!match.Success)
                                    {
                                        client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                                        break;
                                    }

                                    if (short.TryParse(command[2], out var value))
                                    {
                                        client.Tamer.SetSize(value);

                                        if (client.DungeonMap)
                                        {
                                            _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, new UpdateSizePacket(client.Tamer.GeneralHandler, client.Tamer.Size).Serialize());
                                        }
                                        else
                                        {
                                            _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new UpdateSizePacket(client.Tamer.GeneralHandler, client.Tamer.Size).Serialize());

                                        }
                                        await _sender.Send(new UpdateCharacterSizeCommand(client.TamerId, value));
                                    }
                                    else
                                    {
                                        client.Send(new SystemMessagePacket($"Invalid value. Max possible amount is {short.MaxValue}."));
                                    }
                                }
                                break;

                            case "exp":
                                {
                                    //TODO: refazer
                                    var regex = @"(tamer\sexp\sadd\s\d){1}|(tamer\sexp\sremove\s\d){1}|(tamer\sexp\smax){1}";
                                    var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                                    if (!match.Success)
                                    {
                                        client.Send(new SystemMessagePacket("Correct usage is \"!tamer exp add value\" or " +
                                            "\"!tamer exp remove value\"" +
                                            "\"!tamer exp max\".")
                                            .Serialize());

                                        break;
                                    }

                                    switch (command[2])
                                    {
                                        case "max":
                                            {
                                                if (client.Tamer.Level >= (int)GeneralSizeEnum.TamerLevelMax)
                                                {
                                                    client.Send(new SystemMessagePacket($"Tamer already at max level."));
                                                    break;
                                                }

                                                var result = _expManager.ReceiveMaxTamerExperience(client.Tamer);

                                                if (result.Success)
                                                {
                                                    client.Send(
                                                        new ReceiveExpPacket(
                                                            0,
                                                            0,
                                                            client.Tamer.CurrentExperience,
                                                            client.Tamer.Partner.GeneralHandler,
                                                            0,
                                                            0,
                                                            client.Tamer.Partner.CurrentExperience,
                                                            0
                                                        )
                                                    );
                                                }
                                                else
                                                {
                                                    client.Send(new SystemMessagePacket($"No proper configuration for tamer {client.Tamer.Model} leveling."));
                                                    return;
                                                }

                                                if (result.LevelGain > 0)
                                                {
                                                    client.Tamer.SetLevelStatus(
                                                        _statusManager.GetTamerLevelStatus(
                                                            client.Tamer.Model,
                                                            client.Tamer.Level
                                                        )
                                                    );


                                                    _mapServer.BroadcastForTamerViewsAndSelf(
                                                    client.TamerId,
                                                    new LevelUpPacket(
                                                        client.Tamer.GeneralHandler,
                                                        client.Tamer.Level)
                                                    .Serialize());

                                                    client.Tamer.FullHeal();

                                                    client.Send(new UpdateStatusPacket(client.Tamer));
                                                }

                                                if (result.Success)
                                                    await _sender.Send(new UpdateCharacterExperienceCommand(client.TamerId, client.Tamer.CurrentExperience, client.Tamer.Level));
                                            }
                                            break;

                                        case "add":
                                            {
                                                if (client.Tamer.Level >= (int)GeneralSizeEnum.TamerLevelMax)
                                                {
                                                    client.Send(new SystemMessagePacket($"Tamer already at max level."));
                                                    break;
                                                }

                                                var value = Convert.ToInt64(command[3]);

                                                var result = _expManager.ReceiveTamerExperience(value, client.Tamer);

                                                if (result.Success)
                                                {
                                                    client.Send(
                                                        new ReceiveExpPacket(
                                                            value,
                                                            0,
                                                            client.Tamer.CurrentExperience,
                                                            client.Tamer.Partner.GeneralHandler,
                                                            0,
                                                            0,
                                                            client.Tamer.Partner.CurrentExperience,
                                                            0
                                                        )
                                                    );
                                                }
                                                else
                                                {
                                                    client.Send(new SystemMessagePacket($"No proper configuration for tamer {client.Tamer.Model} leveling."));
                                                    return;
                                                }

                                                if (result.LevelGain > 0)
                                                {
                                                    client.Tamer.SetLevelStatus(
                                                        _statusManager.GetTamerLevelStatus(
                                                            client.Tamer.Model,
                                                            client.Tamer.Level
                                                        )
                                                    );

                                                    _mapServer.BroadcastForTamerViewsAndSelf(
                                                    client.TamerId,
                                                    new LevelUpPacket(
                                                        client.Tamer.GeneralHandler,
                                                        client.Tamer.Level).Serialize());

                                                    client.Tamer.FullHeal();

                                                    client.Send(new UpdateStatusPacket(client.Tamer));
                                                }

                                                if (result.Success)
                                                    await _sender.Send(new UpdateCharacterExperienceCommand(client.TamerId, client.Tamer.CurrentExperience, client.Tamer.Level));
                                            }
                                            break;

                                        case "remove":
                                            {
                                                var value = Convert.ToInt64(command[3]);

                                                var tamerInfos = _assets.TamerLevelInfo
                                                    .Where(x => x.Type == client.Tamer.Model)
                                                    .ToList();

                                                if (tamerInfos == null || !tamerInfos.Any() || tamerInfos.Count != (int)GeneralSizeEnum.TamerLevelMax)
                                                {
                                                    _logger.Warning($"Incomplete level config for tamer {client.Tamer.Model}.");

                                                    client.Send(new SystemMessagePacket
                                                        ($"No proper configuration for tamer {client.Tamer.Model} leveling."));
                                                    break;
                                                }

                                                //TODO: ajeitar
                                                client.Tamer.LooseExp(value);

                                                client.Send(new ReceiveExpPacket(
                                                    value * -1,
                                                    0,
                                                    client.Tamer.CurrentExperience,
                                                    client.Tamer.Partner.GeneralHandler,
                                                    0,
                                                    0,
                                                    client.Tamer.Partner.CurrentExperience,
                                                    0
                                                ));

                                                await _sender.Send(new UpdateCharacterExperienceCommand(client.TamerId, client.Tamer.CurrentExperience, client.Tamer.Level));
                                            }
                                            break;


                                        default:
                                            {
                                                client.Send(new SystemMessagePacket("Correct usage is \"!tamer exp add {value}\" or " +
                                                "\"!tamer exp max\"."));
                                            }
                                            break;
                                    }
                                }
                                break;

                            case "summona":
                                {

                                    var TargetSummon = _mapServer.FindClientByTamerName(command[2]);

                                    if (TargetSummon == null)
                                        return;

                                    TargetSummon.Send(new UpdateSizePacket(client.Tamer.Name, client.Tamer.Location.MapId));

                                }
                                break;
                            default:
                                {
                                    client.Send(new SystemMessagePacket("Em desenvolvimento."));
                                }
                                break;
                        }
                    }
                    break;

                case "digimon":
                    {
                        if (command.Length == 1)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                            break;
                        }

                        switch (command[1])
                        {
                            case "transcend":
                                {
                                    var regex = @"(digimon\stranscend){1}";
                                    var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                                    if (!match.Success)
                                    {
                                        client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                                        break;
                                    }

                                    client.Partner.Transcend();
                                    client.Partner.SetSize(14000);

                                    client.Partner.SetBaseStatus(
                                        _statusManager.GetDigimonBaseStatus(
                                            client.Partner.CurrentType,
                                            client.Partner.Level,
                                            client.Partner.Size
                                        )
                                    );

                                    await _sender.Send(new UpdateDigimonSizeCommand(client.Partner.Id, client.Partner.Size));
                                    await _sender.Send(new UpdateDigimonGradeCommand(client.Partner.Id, client.Partner.HatchGrade));
                                }
                                break;

                            case "size":
                                {
                                    var regex = @"(digimon\ssize\s\d){1}";
                                    var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                                    if (!match.Success)
                                    {
                                        client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                                        break;
                                    }

                                    if (short.TryParse(command[2], out var value))
                                    {
                                        client.Partner.SetSize(value);
                                        client.Partner.SetBaseStatus(
                                            _statusManager.GetDigimonBaseStatus(
                                                client.Partner.CurrentType,
                                                client.Partner.Level,
                                                client.Partner.Size
                                            )
                                        );

                                        _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new UpdateSizePacket(client.Partner.GeneralHandler, client.Partner.Size).Serialize());
                                        client.Send(new UpdateStatusPacket(client.Tamer));
                                        await _sender.Send(new UpdateDigimonSizeCommand(client.Partner.Id, value));
                                    }
                                    else
                                    {
                                        client.Send(new SystemMessagePacket($"Invalid value. Max possible amount is {short.MaxValue}."));
                                    }
                                }
                                break;

                            case "exp":
                                {
                                    var regex = @"(digimon\sexp\sadd\s\d){1}|(digimon\sexp\sremove\s\d){1}|(digimon\sexp\smax){1}";
                                    var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                                    if (!match.Success)
                                    {
                                        client.Send(new SystemMessagePacket("Correct usage is \"!digimon exp add value\" or " +
                                            "\"!digimon exp remove value\" or " +
                                            "\"!digimon exp max\".")
                                            .Serialize());

                                        break;
                                    }

                                    switch (command[2])
                                    {
                                        case "max":
                                            {
                                                if (client.Partner.Level >= (int)GeneralSizeEnum.DigimonLevelMax)
                                                {
                                                    client.Send(new SystemMessagePacket($"Partner already at max level."));
                                                    break;
                                                }

                                                var result = _expManager.ReceiveMaxDigimonExperience(client.Partner);

                                                if (result.Success)
                                                {
                                                    client.Send(
                                                        new ReceiveExpPacket(
                                                            0,
                                                            0,
                                                            client.Tamer.CurrentExperience,
                                                            client.Tamer.Partner.GeneralHandler,
                                                            0,
                                                            0,
                                                            client.Tamer.Partner.CurrentExperience,
                                                            0
                                                        )
                                                    );
                                                }
                                                else
                                                {
                                                    client.Send(new SystemMessagePacket($"No proper configuration for digimon {client.Partner.Model} leveling."));
                                                    return;
                                                }

                                                if (result.LevelGain > 0)
                                                {
                                                    client.Partner.SetBaseStatus(
                                                        _statusManager.GetDigimonBaseStatus(
                                                            client.Partner.CurrentType,
                                                            client.Partner.Level,
                                                            client.Partner.Size
                                                        )
                                                    );

                                                    _mapServer.BroadcastForTamerViewsAndSelf(
                                                        client.TamerId,
                                                        new LevelUpPacket(
                                                            client.Tamer.Partner.GeneralHandler,
                                                            client.Tamer.Partner.Level
                                                        ).Serialize()
                                                    );

                                                    client.Partner.FullHeal();

                                                    client.Send(new UpdateStatusPacket(client.Tamer));
                                                }

                                                if (result.Success)
                                                    await _sender.Send(new UpdateDigimonExperienceCommand(client.Partner));
                                            }
                                            break;

                                        case "add":
                                            {
                                                if (client.Partner.Level == (int)GeneralSizeEnum.DigimonLevelMax)
                                                {
                                                    client.Send(new SystemMessagePacket($"Partner already at max level."));
                                                    break;
                                                }

                                                var value = Convert.ToInt64(command[3]);

                                                var result = _expManager.ReceiveDigimonExperience(value, client.Partner);

                                                if (result.Success)
                                                {
                                                    client.Send(
                                                        new ReceiveExpPacket(
                                                            0,
                                                            0,
                                                            client.Tamer.CurrentExperience,
                                                            client.Tamer.Partner.GeneralHandler,
                                                            value,
                                                            0,
                                                            client.Tamer.Partner.CurrentExperience,
                                                            0
                                                        )
                                                    );
                                                }
                                                else
                                                {
                                                    client.Send(new SystemMessagePacket($"No proper configuration for digimon {client.Partner.Model} leveling."));
                                                    return;
                                                }

                                                if (result.LevelGain > 0)
                                                {
                                                    client.Partner.SetBaseStatus(
                                                        _statusManager.GetDigimonBaseStatus(
                                                            client.Partner.CurrentType,
                                                            client.Partner.Level,
                                                            client.Partner.Size
                                                        )
                                                    );

                                                    _mapServer.BroadcastForTamerViewsAndSelf(
                                                        client.TamerId,
                                                        new LevelUpPacket(
                                                            client.Tamer.Partner.GeneralHandler,
                                                            client.Tamer.Partner.Level
                                                        ).Serialize()
                                                    );

                                                    client.Partner.FullHeal();

                                                    client.Send(new UpdateStatusPacket(client.Tamer));
                                                }

                                                if (result.Success)
                                                    await _sender.Send(new UpdateDigimonExperienceCommand(client.Partner));
                                            }
                                            break;

                                        case "remove":
                                            {
                                                var value = Convert.ToInt64(command[3]);

                                                var digimonInfos = _assets.DigimonLevelInfo
                                                    .Where(x => x.Type == client.Tamer.Partner.BaseType)
                                                    .ToList();

                                                if (digimonInfos == null || !digimonInfos.Any() || digimonInfos.Count != (int)GeneralSizeEnum.DigimonLevelMax)
                                                {
                                                    _logger.Warning($"Incomplete level config for digimon {client.Tamer.Partner.BaseType}.");

                                                    client.Send(new SystemMessagePacket
                                                        ($"No proper configuration for digimon {client.Tamer.Partner.BaseType} leveling."));
                                                    break;
                                                }

                                                //TODO: ajeitar
                                                var partnerInitialLevel = client.Partner.Level;

                                                client.Tamer.LooseExp(value);

                                                client.Send(new ReceiveExpPacket(
                                                    0,
                                                    0,
                                                    client.Tamer.CurrentExperience,
                                                    client.Tamer.Partner.GeneralHandler,
                                                    value * -1,
                                                    0,
                                                    client.Tamer.Partner.CurrentExperience,
                                                    0
                                                ));

                                                if (partnerInitialLevel != client.Partner.Level)
                                                    client.Send(new LevelUpPacket(client.Partner.GeneralHandler, client.Partner.Level));

                                                await _sender.Send(new UpdateDigimonExperienceCommand(client.Partner));
                                            }
                                            break;

                                        default:
                                            {
                                                client.Send(new SystemMessagePacket("Correct usage is \"!digimon exp add value\" or " +
                                                "\"!digimon exp max\"."));
                                            }
                                            break;
                                    }
                                }
                                break;

                            default:
                                {
                                    client.Send(new SystemMessagePacket("Unknown command. Check the available commands at the admin portal."));
                                }
                                break;
                        }
                    }
                    break;

                case "teleport":
                    {
                        var regex = @"(teleport\s\d\s\d){1}|(teleport\s\d){1}";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                            break;
                        }

                        var mapId = Convert.ToInt32(command[1]);
                        var waypoint = command.Length == 3 ? Convert.ToInt32(command[2]) : 0;

                        var mapConfig = await _sender.Send(new GameMapConfigByMapIdQuery(mapId));
                        var waypoints = await _sender.Send(new MapRegionListAssetsByMapIdQuery(mapId));
                        if (mapConfig == null || waypoints == null || !waypoints.Regions.Any())
                        {
                            client.Send(new SystemMessagePacket($"Map information not found for ID {mapId}"));
                            break;
                        }

                        if (client.DungeonMap)
                            _dungeonServer.RemoveClient(client);
                        else
                            _mapServer.RemoveClient(client);

                        var destination = waypoints.Regions.First();

                        if (waypoint > waypoints.Regions.Count)
                            waypoint = waypoints.Regions.Count;

                        if (waypoint > 0)
                            destination = waypoints.Regions[waypoint - 1] ?? destination;

                        client.Tamer.NewLocation(mapId, destination.X, destination.Y);
                        await _sender.Send(new UpdateCharacterLocationCommand(client.Tamer.Location));

                        client.Tamer.Partner.NewLocation(mapId, destination.X, destination.Y);
                        await _sender.Send(new UpdateDigimonLocationCommand(client.Tamer.Partner.Location));

                        client.Tamer.UpdateState(CharacterStateEnum.Loading);
                        await _sender.Send(new UpdateCharacterStateCommand(client.TamerId, CharacterStateEnum.Loading));

                        client.SetGameQuit(false);

                        client.Send(new MapSwapPacket(
                            _configuration[GamerServerPublic],
                            _configuration[GameServerPort],
                            client.Tamer.Location.MapId,
                            client.Tamer.Location.X,
                            client.Tamer.Location.Y)
                            .Serialize());

                        var party = _partyManager.FindParty(client.TamerId);

                        if (party != null)
                        {
                            party.UpdateMember(party[client.TamerId], client.Tamer);

                            foreach (var target in party.Members.Values)
                            {
                                var targetClient = _mapServer.FindClientByTamerId(target.Id);

                                if (targetClient == null) targetClient = _dungeonServer.FindClientByTamerId(target.Id);

                                if (targetClient == null) continue;

                                if (target.Id != client.Tamer.Id)
                                    targetClient.Send(new PartyMemberWarpGatePacket(party[client.TamerId]).Serialize());
                            }
                        }
                    }
                    break;

                case "currency":
                    {
                        var regex = @"(currency\sbits\s\d){1}|(currency\spremium\s\d){1}|(currency\ssilk\s\d){1}";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                            break;
                        }

                        switch (command[1])
                        {
                            case "bits":
                                {
                                    var value = long.Parse(command[2]);
                                    client.Tamer.Inventory.AddBits(value);

                                    client.Send(new LoadInventoryPacket(client.Tamer.Inventory,
                                        InventoryTypeEnum.Inventory)
                                        .Serialize());

                                    await _sender.Send(new UpdateItemListBitsCommand(client.Tamer.Inventory.Id, client.Tamer.Inventory.Bits));
                                }
                                break;

                            case "premium":
                                {
                                    var value = int.Parse(command[2]);
                                    client.AddPremium(value);

                                    await _sender.Send(new UpdatePremiumAndSilkCommand(client.Premium,
                                        client.Silk, client.AccountId));
                                }
                                break;

                            case "silk":
                                {
                                    var value = int.Parse(command[2]);
                                    client.AddSilk(value);

                                    await _sender.Send(new UpdatePremiumAndSilkCommand(client.Premium, client.Silk, client.AccountId));
                                }
                                break;
                        }
                    }
                    break;

                case "pvp":
                    {
                        if (client.Tamer.PvpMap == false)
                        {
                            client.Tamer.PvpMap = true;
                            client.Send(new NoticeMessagePacket($"PVP do seu Personagem foi ativado!"));
                        }
                        else
                        {
                            client.Tamer.PvpMap = false;
                            client.Send(new NoticeMessagePacket($"PVP do seu Personagem foi desativado!"));
                        }
                    }
                    break;

                case "reload":
                    {
                        var regex = @"(reload$){1}";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                            break;
                        }

                        _logger.Debug($"Updating tamer state...");
                        client.Tamer.UpdateState(CharacterStateEnum.Loading);
                        await _sender.Send(new UpdateCharacterStateCommand(client.TamerId, CharacterStateEnum.Loading));

                        _mapServer.RemoveClient(client);

                        client.SetGameQuit(false);
                        client.Tamer.UpdateSlots();

                        client.Send(new MapSwapPacket(
                            _configuration[GamerServerPublic],
                            _configuration[GameServerPort],
                            client.Tamer.Location.MapId,
                            client.Tamer.Location.X,
                            client.Tamer.Location.Y));
                    }
                    break;

                case "dc":
                    {
                        var regex = @"^dc\s[\w\s]+$";
                        var match = Regex.Match(message, regex, RegexOptions.None);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                            break;
                        }
                        string[] comando = message.Split(' ');
                        var TamerName = comando[1];

                        var targetClient = _mapServer.FindClientByTamerName(TamerName);
                        if (targetClient == null) targetClient = _dungeonServer.FindClientByTamerName(TamerName);

                        if (targetClient == null)
                        {
                            client.Send(new SystemMessagePacket($"Player {TamerName} not Online!"));
                            break;
                        }

                        if (client.Tamer.Name == TamerName)
                        {
                            client.Send(new SystemMessagePacket($"You are a {TamerName}!"));
                            break;
                        }

                        await _mapServer.CallDiscord($"{client.Tamer.Name} acaba de kickar {targetClient.Tamer.Name}.", client, "46ff00", "KICK", "1280704020469514291", true);
                        targetClient.Send(new SystemMessagePacket($"Voce foi kickado pela staff!"));
                        targetClient.Disconnect();

                    }
                    break;

                case "heal":
                    {
                        var regex = @"^heal\s*$";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command.\nType !heal"));
                            break;
                        }

                        client.Tamer.FullHeal();
                        client.Tamer.Partner.FullHeal();

                        client.Send(new UpdateStatusPacket(client.Tamer));
                        await _sender.Send(new UpdateCharacterBasicInfoCommand(client.Tamer));
                    }
                    break;

                case "stats":
                    {
                        var regex = @"^stats\s*$";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command.\nType !stats"));
                            break;
                        }

                        client.Send(new SystemMessagePacket($"Critical Damage: {client.Tamer.Partner.CD / 100}%\n" +
                            $"Attribute Damage: {client.Tamer.Partner.ATT / 100}%\n" +
                            $"Digimon SKD: {client.Tamer.Partner.SKD}\n" +
                            $"Digimon SCD: {client.Tamer.Partner.SCD / 100}%\n" +
                            $"Tamer BonusEXP: {client.Tamer.BonusEXP}%\n" +
                            $"Tamer Move Speed: {client.Tamer.MS}"));

                    }
                    break;

                case "ban":
                    {
                        if (command.Length == 1)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                            break;
                        }

                        var otherText = string.Join(" ", message.Split(' ').Skip(1));
                        string targetName = command[1];
                        string Time = command[2];
                        string banReason = string.Join(" ", otherText.Split(' ').Skip(2));

                        if (string.IsNullOrEmpty(banReason) || string.IsNullOrEmpty(Time))
                        {
                            client.Send(new SystemMessagePacket($"Incorret command, use \"!ban TamerName Horas BanReason\"."));

                            return;
                        }

                        if (!string.IsNullOrEmpty(targetName))
                        {

                            var TargetBan = await _sender.Send(new CharacterByNameQuery(targetName));
                            if (TargetBan == null)
                            {
                                _logger.Warning($"Character not found with name {targetName}.");
                                client.Send(new SystemMessagePacket($"Character not found with name {targetName}."));
                                return;
                            }
                            var TargetBanId = await _sender.Send(new AccountByIdQuery(TargetBan.AccountId));

                            if (TargetBan == null)
                            {
                                client.Send(new SystemMessagePacket($"User not found with name {targetName}."));
                                return;
                            }
                            try
                            {
                                int TimeBan = int.Parse(Time);
                                var newBan = AccountBlockModel.Create((int)TargetBan.AccountId, banReason, AccountBlockEnum.Permannent, DateTime.Now.AddHours(TimeBan));

                                try
                                {
                                    var isBanned = await _sender.Send(new CreateNewBanCommand(newBan));
                                    if (isBanned != null)
                                    {
                                        await _mapServer.CallDiscord($"{client.Tamer.Name} acaba de banir {TargetBan.Name} por {TimeBan} horas. Motivo: {banReason}.", client, "7981ff", "BAN", "1280704020469514291", true);
                                        var banMessage = "A Tamer has been banned. We're keeping our community clean!";
                                        _mapServer.BroadcastGlobal(new ChatMessagePacket(banMessage, ChatTypeEnum.Megaphone, "[System]", 52, 120).Serialize());
                                        _dungeonServer.BroadcastGlobal(new ChatMessagePacket(banMessage, ChatTypeEnum.Megaphone, "[System]", 52, 120).Serialize());

                                        var TargetTamer = _mapServer.FindClientByTamerId(TargetBan.Id);
                                        if (TargetTamer != null)
                                        {
                                            _logger.Information($"Found client {TargetTamer.ClientAddress} Tamer ID: {TargetTamer.TamerId}");

                                            TimeSpan timeRemaining = isBanned.EndDate - DateTime.Now;

                                            uint secondsRemaining = (uint)timeRemaining.TotalSeconds;

                                            TargetTamer.Send(new BanUserPacket(secondsRemaining, isBanned.Reason));

                                            var party = _partyManager.FindParty(TargetTamer.TamerId);
                                            if (party != null)
                                            {
                                                var member = party.Members.FirstOrDefault(x => x.Value.Id == TargetTamer.TamerId);

                                                foreach (var target in party.Members.Values)
                                                {
                                                    var targetClient = _mapServer.FindClientByTamerId(target.Id);
                                                    if (targetClient == null) targetClient = _dungeonServer.FindClientByTamerId(target.Id);

                                                    if (targetClient == null) continue;
                                                    if (target.Id != client.Tamer.Id) targetClient.Send(new PartyMemberWarpGatePacket(party[client.TamerId]).Serialize());
                                                }
                                            }

                                        }
                                        else
                                        {

                                            _logger.Information($"Banned client is not found");
                                        }
                                        break;
                                    }
                                    else
                                    {

                                        client.Send(new SystemMessagePacket($"SERVER: There was an error trying to ban the user {targetName}, Check and try again."));
                                        return;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.Error($"Unexpected error on friend create request. Ex.: {ex.Message}. Stack: {ex.StackTrace}");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Information("Error on Banning: {ex}", ex.Message);

                            }
                        }
                    }
                    break;
                case "pos":
                    {

                        client.Send(new SystemMessagePacket($"Loc MapID: {client.Tamer.Location.MapId} X: {client.Tamer.Location.X} AND Y: {client.Tamer.Location.Y}."));
                        return;
                    }
                    break;

                case "exit":
                    {
                        var regex = @"^exit\s*$";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                            break;
                        }

                        if (client.Tamer.Location.MapId == 89)
                        {
                            var mapId = 3;

                            var mapConfig = await _sender.Send(new GameMapConfigByMapIdQuery(mapId));
                            var waypoints = await _sender.Send(new MapRegionListAssetsByMapIdQuery(mapId));

                            if (client.DungeonMap)
                                _dungeonServer.RemoveClient(client);
                            else
                                _mapServer.RemoveClient(client);

                            var destination = waypoints.Regions.First();

                            client.Tamer.NewLocation(mapId, destination.X, destination.Y);
                            await _sender.Send(new UpdateCharacterLocationCommand(client.Tamer.Location));

                            client.Tamer.Partner.NewLocation(mapId, destination.X, destination.Y);
                            await _sender.Send(new UpdateDigimonLocationCommand(client.Tamer.Partner.Location));

                            client.Tamer.UpdateState(CharacterStateEnum.Loading);
                            await _sender.Send(new UpdateCharacterStateCommand(client.TamerId, CharacterStateEnum.Loading));

                            client.SetGameQuit(false);

                            client.Send(new MapSwapPacket(_configuration[GamerServerPublic], _configuration[GameServerPort],
                                client.Tamer.Location.MapId, client.Tamer.Location.X, client.Tamer.Location.Y).Serialize());
                        }
                        else
                        {
                            client.Send(new SystemMessagePacket($"Este comando so pode ser usado no Mapa de Evento !!"));
                            break;
                        }

                    }
                    break;

                case "rassets":
                    {
                        var assetsLoader = _assets.Reload();
                        return;
                    }
                    break;
                case "tpto":
                    {
                        var regex = @"^tpto\s[\w\s]+$";
                        var match = Regex.Match(message, regex, RegexOptions.None);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                            break;
                        }
                        string[] comando = message.Split(' ');
                        var TamerName = comando[1];

                        var targetClient = _mapServer.FindClientByTamerName(TamerName);
                        var targetClientD = _dungeonServer.FindClientByTamerName(TamerName);

                        if (targetClient == null && targetClientD == null)
                        {
                            client.Send(new SystemMessagePacket($"Player {TamerName} not Online!"));
                            break;
                        }

                        if (client.Tamer.Name == TamerName)
                        {
                            client.Send(new SystemMessagePacket($"You are a {TamerName}!"));
                            break;
                        }

                        var map = _mapServer.Maps.FirstOrDefault(x => x.Clients.Exists(x => x.Tamer.Name == TamerName));
                        if (map != null)
                        {
                            if (client.DungeonMap)
                                _dungeonServer.RemoveClient(client);
                            else
                                _mapServer.RemoveClient(client);

                            var destination = targetClient.Tamer.Location;
                            client.Tamer.SetTamerTP(targetClient.TamerId);
                            await _sender.Send(new ChangeTamerIdTPCommand(client.Tamer.Id, (int)targetClient.TamerId));


                            client.Tamer.NewLocation(destination.MapId, destination.X, destination.Y);
                            await _sender.Send(new UpdateCharacterLocationCommand(client.Tamer.Location));

                            client.Tamer.Partner.NewLocation(destination.MapId, destination.X, destination.Y);
                            await _sender.Send(new UpdateDigimonLocationCommand(client.Tamer.Partner.Location));

                            client.Tamer.UpdateState(CharacterStateEnum.Loading);
                            await _sender.Send(new UpdateCharacterStateCommand(client.TamerId, CharacterStateEnum.Loading));

                            client.SetGameQuit(false);

                            client.Send(new MapSwapPacket(
                                _configuration[GamerServerPublic],
                                _configuration[GameServerPort],
                                client.Tamer.Location.MapId,
                                client.Tamer.Location.X,
                                client.Tamer.Location.Y)
                                .Serialize());

                            var party = _partyManager.FindParty(client.TamerId);

                            if (party != null)
                            {
                                party.UpdateMember(party[client.TamerId], client.Tamer);

                                _dungeonServer.BroadcastForTargetTamers(party.GetMembersIdList(), new PartyMemberWarpGatePacket(party[client.TamerId]).Serialize());
                            }
                        }
                        else
                        {
                            var mapdg = _dungeonServer.Maps.FirstOrDefault(x => x.Clients.Exists(x => x.TamerId == targetClientD.TamerId));
                            client.Tamer.SetTamerTP(targetClientD.TamerId);
                            await _sender.Send(new ChangeTamerIdTPCommand(client.Tamer.Id, (int)targetClientD.TamerId));

                            if (client.DungeonMap)
                                _dungeonServer.RemoveClient(client);
                            else
                                _mapServer.RemoveClient(client);

                            var destination = targetClientD.Tamer.Location;

                            client.Tamer.NewLocation(destination.MapId, destination.X, destination.Y);
                            await _sender.Send(new UpdateCharacterLocationCommand(client.Tamer.Location));

                            client.Tamer.Partner.NewLocation(destination.MapId, destination.X, destination.Y);
                            await _sender.Send(new UpdateDigimonLocationCommand(client.Tamer.Partner.Location));
                            client.Tamer.SetCurrentChannel(targetClientD.Tamer.Channel);

                            client.Tamer.UpdateState(CharacterStateEnum.Loading);
                            await _sender.Send(new UpdateCharacterStateCommand(client.TamerId, CharacterStateEnum.Loading));

                            client.SetGameQuit(false);

                            client.Send(new MapSwapPacket(
                                _configuration[GamerServerPublic],
                                _configuration[GameServerPort],
                                client.Tamer.Location.MapId,
                                client.Tamer.Location.X,
                                client.Tamer.Location.Y)
                                .Serialize());

                            var party = _partyManager.FindParty(client.TamerId);

                            if (party != null)
                            {
                                party.UpdateMember(party[client.TamerId], client.Tamer);

                                _dungeonServer.BroadcastForTargetTamers(party.GetMembersIdList(), new PartyMemberWarpGatePacket(party[client.TamerId]).Serialize());
                            }
                        }

                    }
                    break;

                case "item":
                    {
                        var regex = @"(item\s\d{1,7}\s\d{1,4}$){1}|(item\s\d{1,7}$){1}";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                            break;
                        }

                        var itemId = int.Parse(command[1]);

                        var newItem = new ItemModel();
                        newItem.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == itemId));

                        if (newItem.ItemInfo == null)
                        {
                            _logger.Warning($"No item info found with ID {itemId} for tamer {client.TamerId}.");
                            client.Send(new SystemMessagePacket($"No item info found with ID {itemId}."));
                            break;
                        }

                        newItem.ItemId = itemId;
                        newItem.Amount = command.Length == 2 ? 1 : int.Parse(command[2]);

                        if (newItem.IsTemporary)
                            newItem.SetRemainingTime((uint)newItem.ItemInfo.UsageTimeMinutes);

                        var itemClone = (ItemModel)newItem.Clone();
                        if (client.Tamer.Inventory.AddItem(newItem))
                        {
                            client.Send(new ReceiveItemPacket(newItem, InventoryTypeEnum.Inventory));
                            await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
                        }
                        else
                        {
                            client.Send(new PickItemFailPacket(PickItemFailReasonEnum.InventoryFull));
                        }
                    }
                    break;

                case "gfstorage":
                    {
                        var regex = @"^(gfstorage\s(add\s\d{1,7}\s\d{1,4}|clear))$";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command.\nType: gfstorage add itemId Amount or gfstorage clear"));
                            break;
                        }

                        if (command[1] == "clear")
                        {
                            client.Tamer.GiftWarehouse.Clear();

                            client.Send(new SystemMessagePacket($" GiftStorage slots cleaned."));
                            client.Send(new LoadGiftStoragePacket(client.Tamer.GiftWarehouse));

                            await _sender.Send(new UpdateItemsCommand(client.Tamer.GiftWarehouse));
                        }
                        else if (command[1] == "add")
                        {
                            if (!int.TryParse(command[2], out var itemId))
                            {
                                client.Send(new SystemMessagePacket($"Invalid ItemID format."));
                                break;
                            }

                            var amount = command.Length == 3 ? 1 : (int.TryParse(command[3], out var parsedAmount) ? parsedAmount : 1);

                            var newItem = new ItemModel();
                            newItem.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == itemId));

                            if (newItem.ItemInfo == null)
                            {
                                _logger.Warning($"No item info found with ID {itemId} for tamer {client.TamerId}.");
                                client.Send(new SystemMessagePacket($"No item info found with ID {itemId}."));
                                break;
                            }

                            newItem.ItemId = itemId;
                            newItem.Amount = amount;

                            if (newItem.IsTemporary)
                                newItem.SetRemainingTime((uint)newItem.ItemInfo.UsageTimeMinutes);

                            newItem.EndDate = DateTime.Now.AddDays(7);

                            if (client.Tamer.GiftWarehouse.AddItemGiftStorage(newItem))
                            {
                                await _sender.Send(new UpdateItemsCommand(client.Tamer.GiftWarehouse));
                                client.Send(new SystemMessagePacket($"Added x{amount} item {itemId} to GiftStorage."));
                            }
                            else
                            {
                                client.Send(new SystemMessagePacket($"Could not add item {itemId} to GiftStorage. Slots may be full."));
                            }
                        }
                    }
                    break;

                case "cashstorage":
                    {
                        var regex = @"^(cashstorage\s(add\s\d{1,7}\s\d{1,4}|clear))$";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command.\nType: cashstorage add itemId Amount or cashstorage clear"));
                            break;
                        }

                        if (command[1] == "clear")
                        {
                            client.Tamer.AccountCashWarehouse.Clear();

                            client.Send(new SystemMessagePacket($" CashStorage slots cleaned."));
                            client.Send(new LoadAccountWarehousePacket(client.Tamer.AccountCashWarehouse));

                            await _sender.Send(new UpdateItemsCommand(client.Tamer.AccountCashWarehouse));
                        }
                        else if (command[1] == "add")
                        {
                            if (!int.TryParse(command[2], out var itemId))
                            {
                                client.Send(new SystemMessagePacket($"Invalid ItemID format."));
                                break;
                            }

                            var amount = command.Length == 3 ? 1 : (int.TryParse(command[3], out var parsedAmount) ? parsedAmount : 1);

                            var newItem = new ItemModel();
                            newItem.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == itemId));

                            if (newItem.ItemInfo == null)
                            {
                                _logger.Warning($"No item info found with ID {itemId} for tamer {client.TamerId}.");
                                client.Send(new SystemMessagePacket($"No item info found with ID {itemId}."));
                                break;
                            }

                            newItem.ItemId = itemId;
                            newItem.Amount = amount;

                            if (newItem.IsTemporary)
                                newItem.SetRemainingTime((uint)newItem.ItemInfo.UsageTimeMinutes);

                            var itemClone = (ItemModel)newItem.Clone();

                            if (client.Tamer.AccountCashWarehouse.AddItemGiftStorage(newItem))
                            {
                                await _sender.Send(new UpdateItemsCommand(client.Tamer.AccountCashWarehouse));
                                client.Send(new SystemMessagePacket($"Added item {itemId} x{amount} to CashStorage."));
                            }
                            else
                            {
                                client.Send(new SystemMessagePacket($"Could not add item {itemId} to CashStorage. Slots may be full."));
                            }
                        }
                    }
                    break;


                case "hide":
                    {
                        var regex = @"(hide$){1}";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                            break;
                        }

                        if (client.Tamer.Hidden)
                        {
                            client.Send(new SystemMessagePacket($"You are already in hide mode."));
                        }
                        else
                        {
                            client.Tamer.SetHidden(true);
                            client.Send(new SystemMessagePacket($"View state has been set to hide mode."));
                        }
                    }
                    break;

                case "show":
                    {
                        var regex = @"(show$){1}";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                            break;
                        }

                        if (client.Tamer.Hidden)
                        {
                            client.Tamer.SetHidden(false);
                            client.Send(new SystemMessagePacket($"View state has been set to show mode."));
                        }
                        else
                        {
                            client.Send(new SystemMessagePacket($"You are already in show mode."));
                        }
                    }
                    break;

                case "inventory":
                    {
                        var regex = @"(inv\sslots\sadd\s\d{1,3}$){1}|(inventory\sslots\sadd\s\d{1,3}$){1}|(inventory\sslots\sclear$){1}|(inv\sslots\sclear$){1}";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                            break;
                        }

                        if (command[2] == "add")
                        {
                            if (byte.TryParse(command[3], out byte targetSize))
                            {
                                if (targetSize == byte.MinValue)
                                {
                                    client.Send(new SystemMessagePacket($"Invalid slots amount. Check your command on the Admin Portal."));
                                    break;
                                }

                                var newSize = client.Tamer.Inventory.AddSlots(targetSize);

                                client.Send(new SystemMessagePacket($"Inventory slots updated to {newSize}."));
                                client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));

                                var newSlots = client.Tamer.Inventory.Items.Where(x => x.ItemList == null).ToList();
                                await _sender.Send(new AddInventorySlotsCommand(newSlots));
                                newSlots.ForEach(newSlot => { newSlot.ItemList = client.Tamer.Inventory.Items.First(x => x.ItemList != null).ItemList; });
                                await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
                            }
                            else
                            {
                                client.Send(new SystemMessagePacket($"Invalid command parameters. Check the available commands on the Admin Portal."));
                                break;
                            }
                        }
                        else if (command[2] == "clear")
                        {
                            client.Tamer.Inventory.Clear();
                            client.Send(new SystemMessagePacket($"Inventory slots cleaned."));
                            client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));

                            await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
                        }
                    }
                    break;

                case "giftstorage":
                    {
                        var regex = @"(inv\sslots\sadd\s\d{1,3}$){1}|(giftstorage\sslots\sadd\s\d{1,3}$){1}|(giftstorage\sslots\sclear$){1}|(gif\sslots\sclear$){1}";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                            break;
                        }

                        if (command[2] == "add")
                        {
                            if (byte.TryParse(command[3], out byte targetSize))
                            {
                                if (targetSize == byte.MinValue)
                                {
                                    client.Send(new SystemMessagePacket($"Invalid slots amount. Check your command on the Admin Portal."));
                                    break;
                                }

                                var newSize = client.Tamer.Inventory.AddSlots(targetSize);

                                client.Send(new SystemMessagePacket($"Inventory slots updated to {newSize}."));
                                client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory));

                                var newSlots = client.Tamer.Inventory.Items.Where(x => x.ItemList == null).ToList();
                                await _sender.Send(new AddInventorySlotsCommand(newSlots));
                                newSlots.ForEach(newSlot => { newSlot.ItemList = client.Tamer.Inventory.Items.First(x => x.ItemList != null).ItemList; });
                                await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
                            }
                            else
                            {
                                client.Send(new SystemMessagePacket($"Invalid command parameters. Check the available commands on the Admin Portal."));
                                break;
                            }
                        }
                        else if (command[2] == "clear")
                        {
                            client.Tamer.GiftWarehouse.Clear();
                            client.Send(new SystemMessagePacket($" GiftStorage slots cleaned."));
                            client.Send(new LoadGiftStoragePacket(client.Tamer.GiftWarehouse));

                            await _sender.Send(new UpdateItemsCommand(client.Tamer.GiftWarehouse));
                        }
                    }
                    break;

                case "godmode":
                    {
                        var regex = @"(godmode\son$){1}|(godmode\soff$){1}";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                            break;
                        }

                        if (command[1] == "on")
                        {
                            if (client.Tamer.GodMode)
                            {
                                client.Send(new SystemMessagePacket($"You are already in god mode."));
                            }
                            else
                            {
                                client.Tamer.SetGodMode(true);
                                client.Send(new SystemMessagePacket($"God mode enabled."));
                            }
                        }
                        else
                        {
                            if (!client.Tamer.GodMode)
                            {
                                client.Send(new SystemMessagePacket($"You are already with god mode disabled."));
                            }
                            else
                            {
                                client.Tamer.SetGodMode(false);
                                client.Send(new SystemMessagePacket($"God mode disabled."));
                            }
                        }
                    }
                    break;

                case "unlockevos":
                    {
                        var regex = @"^unlockevos";
                        var match = Regex.IsMatch(message, regex, RegexOptions.IgnoreCase);

                        if (!match)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command.\nType !unlockevos"));
                            break;
                        }

                        // -- Unlock Digimon Evolutions

                        foreach (var evolution in client.Partner.Evolutions)
                        {
                            evolution.Unlock();
                            await _sender.Send(new UpdateEvolutionCommand(evolution));
                        }

                        // Unlock Digimon Evolutions on Encyclopedia

                        var evoInfo = _assets.EvolutionInfo.FirstOrDefault(x => x.Type == client.Partner.BaseType)?.Lines.FirstOrDefault(x => x.Type == client.Partner.CurrentType);

                        if (evoInfo == null)
                        {
                            _logger.Error($"evoInfo not found !! [ Unlockevos Command ]");
                        }
                        else
                        {
                            var encyclopedia = client.Tamer.Encyclopedia.FirstOrDefault(x => x.DigimonEvolutionId == evoInfo?.EvolutionId);

                            if (encyclopedia == null)
                            {
                                _logger.Error($"encyclopedia not found !! [ Unlockevos Command ]");
                            }
                            else
                            {
                                foreach (var evolution in client.Partner.Evolutions)
                                {
                                    var encyclopediaEvolution = encyclopedia.Evolutions.First(x => x.DigimonBaseType == evolution.Type);

                                    encyclopediaEvolution.Unlock();

                                    await _sender.Send(new UpdateCharacterEncyclopediaEvolutionsCommand(encyclopediaEvolution));
                                }

                                int LockedEncyclopediaCount = encyclopedia.Evolutions.Count(x => x.IsUnlocked == false);

                                if (LockedEncyclopediaCount <= 0)
                                {
                                    try
                                    {
                                        encyclopedia.SetRewardAllowed();
                                        await _sender.Send(new UpdateCharacterEncyclopediaCommand(encyclopedia));
                                    }
                                    catch (Exception ex)
                                    {
                                        //_logger.Error($"LockedEncyclopediaCount Error:\n{ex.Message}");
                                    }
                                }
                            }
                        }

                        // -- Reloading Map

                        client.Tamer.UpdateState(CharacterStateEnum.Loading);
                        await _sender.Send(new UpdateCharacterStateCommand(client.TamerId, CharacterStateEnum.Loading));

                        _mapServer.RemoveClient(client);

                        client.SetGameQuit(false);
                        client.Tamer.UpdateSlots();

                        client.Send(new MapSwapPacket(_configuration[GamerServerPublic], _configuration[GameServerPort],
                            client.Tamer.Location.MapId, client.Tamer.Location.X, client.Tamer.Location.Y));
                    }
                    break;

                case "openseals":
                    {
                        var sealInfoList = _assets.SealInfo;
                        foreach (var seal in sealInfoList)
                        {
                            client.Tamer.SealList.AddOrUpdateSeal(seal.SealId, 3000, seal.SequentialId);
                        }

                        client.Partner?.SetSealStatus(sealInfoList);

                        client.Send(new UpdateStatusPacket(client.Tamer));

                        await _sender.Send(new UpdateCharacterSealsCommand(client.Tamer.SealList));

                        client.Tamer.UpdateState(CharacterStateEnum.Loading);
                        await _sender.Send(new UpdateCharacterStateCommand(client.TamerId, CharacterStateEnum.Loading));

                        _mapServer.RemoveClient(client);

                        client.SetGameQuit(false);

                        client.Send(new MapSwapPacket(
                            _configuration[GamerServerPublic],
                            _configuration[GameServerPort],
                            client.Tamer.Location.MapId,
                            client.Tamer.Location.X,
                            client.Tamer.Location.Y));
                    }
                    break;

                case "membership":
                    {
                        var regex = @"membership\s(add|remove)(\s\d{1,9})?$";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                            break;
                        }

                        switch (command[1])
                        {
                            case "add":
                                {
                                    var valueInDays = int.Parse(command[2]);

                                    var value = valueInDays * 24 * 3600;

                                    client.IncreaseMembershipDuration(value);

                                    var buff = _assets.BuffInfo.Where(x => x.BuffId == 50121 || x.BuffId == 50122 || x.BuffId == 50123).ToList();

                                    int duration = client.MembershipUtcSecondsBuff;

                                    buff.ForEach(buffAsset =>
                                    {
                                        if (!client.Tamer.BuffList.Buffs.Any(x => x.BuffId == buffAsset.BuffId))
                                        {
                                            var newCharacterBuff = CharacterBuffModel.Create(buffAsset.BuffId, buffAsset.SkillId, 2592000, duration);

                                            newCharacterBuff.SetBuffInfo(buffAsset);

                                            client.Tamer.BuffList.Buffs.Add(newCharacterBuff);

                                            _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new AddBuffPacket(client.Tamer.GeneralHandler, buffAsset, 0, duration).Serialize());
                                        }
                                        else
                                        {
                                            var buffData = client.Tamer.BuffList.Buffs.First(x => x.BuffId == buffAsset.BuffId);

                                            if (buffData != null)
                                            {
                                                buffData.SetDuration(duration, true);

                                                _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new UpdateBuffPacket(client.Tamer.GeneralHandler, buffAsset, 0, duration).Serialize());
                                            }
                                        }
                                    });

                                    client.Send(new MembershipPacket(client.MembershipExpirationDate!.Value, duration));
                                    client.Send(new UpdateStatusPacket(client.Tamer));

                                    await _sender.Send(new UpdateAccountMembershipCommand(client.AccountId, client.MembershipExpirationDate));
                                    await _sender.Send(new UpdateCharacterBuffListCommand(client.Tamer.BuffList));

                                    // Reloading Map

                                    client.Tamer.UpdateState(CharacterStateEnum.Loading);
                                    await _sender.Send(new UpdateCharacterStateCommand(client.TamerId, CharacterStateEnum.Loading));

                                    _mapServer.RemoveClient(client);

                                    client.SetGameQuit(false);
                                    client.Tamer.UpdateSlots();

                                    client.Send(new MapSwapPacket(_configuration[GamerServerPublic], _configuration[GameServerPort],
                                        client.Tamer.Location.MapId, client.Tamer.Location.X, client.Tamer.Location.Y));
                                }
                                break;

                            case "remove":
                                {
                                    client.RemoveMembership();

                                    int duration = client.MembershipUtcSecondsBuff;

                                    client.Send(new MembershipPacket());

                                    await _sender.Send(new UpdateAccountMembershipCommand(client.AccountId, client.MembershipExpirationDate));

                                    var secondsUTC = (client.MembershipExpirationDate.Value - DateTime.UtcNow).TotalSeconds;

                                    if (secondsUTC <= 0)
                                    {
                                        //_logger.Information($"Verifying if tamer have buffs without membership");

                                        var buff = _assets.BuffInfo.Where(x => x.BuffId == 50121 || x.BuffId == 50122 || x.BuffId == 50123).ToList();

                                        buff.ForEach(buffAsset =>
                                        {
                                            if (client.Tamer.BuffList.Buffs.Any(x => x.BuffId == buffAsset.BuffId))
                                            {
                                                var buffData = client.Tamer.BuffList.Buffs.First(x => x.BuffId == buffAsset.BuffId);

                                                if (buffData != null)
                                                {
                                                    buffData.SetDuration(0, true);

                                                    _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new UpdateBuffPacket(client.Tamer.GeneralHandler, buffAsset, 0, 0).Serialize());
                                                }
                                            }
                                        });

                                        await _sender.Send(new UpdateCharacterBuffListCommand(client.Tamer.BuffList));
                                    }

                                    client.Send(new UpdateStatusPacket(client.Tamer));

                                    // -- RELOAD -------------------------

                                    client.Tamer.UpdateState(CharacterStateEnum.Loading);
                                    await _sender.Send(new UpdateCharacterStateCommand(client.TamerId, CharacterStateEnum.Loading));

                                    _mapServer.RemoveClient(client);

                                    client.SetGameQuit(false);
                                    client.Tamer.UpdateSlots();

                                    client.Send(new MapSwapPacket(_configuration[GamerServerPublic], _configuration[GameServerPort],
                                        client.Tamer.Location.MapId, client.Tamer.Location.X, client.Tamer.Location.Y));
                                }
                                break;

                            default:
                                client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                                break;
                        }
                    }
                    break;

                case "summon":
                    {
                        var regex = @"(summon\s\d\s\d){1}|(summon\s\d){1}";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                            break;
                        }

                        var MobId = int.Parse(command[1]);

                        var SummonInfo = _assets.SummonInfo.FirstOrDefault(x => x.ItemId == MobId);

                        if (SummonInfo != null) await SummonMonster(client, SummonInfo);
                    }
                    break;

                // -- TOOLS --------------------------------------

                case "tools":
                    {
                        var regex = @"^tools\s*$";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command.\nType !tools"));
                            break;
                        }

                        client.Send(new SystemMessagePacket($"Tools Commands:", "").Serialize());
                        client.Send(new SystemMessagePacket($"1. !fullacc\n2. !evopack\n3. !spacepack\n4. !clon (type) (value)", "").Serialize());
                    }
                    break;

                case "fullacc":
                    {
                        await AddItemToInventory(client, 50, 1);        // 
                        await AddItemToInventory(client, 89143, 1);     // 
                        await AddItemToInventory(client, 40011, 1);     // 
                        await AddItemToInventory(client, 41038, 1);     // Jogress Chip
                        await AddItemToInventory(client, 131063, 1);    // XAI Ver VI
                        await AddItemToInventory(client, 41113, 1);     // DigiAuraBox
                        await AddItemToInventory(client, 41002, 50);    // Accelerator
                        await AddItemToInventory(client, 71594, 20);    // X-Antibody

                        #region BITS (100T)

                        client.Tamer.Inventory.AddBits(100000000);

                        client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize());

                        await _sender.Send(new UpdateItemListBitsCommand(client.Tamer.Inventory.Id, client.Tamer.Inventory.Bits));

                        #endregion

                    }
                    break;

                case "evopack":
                    {
                        var regex = @"^evopack\s*$";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command.\nType !evopack"));
                            break;
                        }

                        await AddItemToInventory(client, 41002, 20);    // Accelerator
                        await AddItemToInventory(client, 41000, 20);    // Spirit Accelerator
                        await AddItemToInventory(client, 5001, 20);     // Evoluter
                        await AddItemToInventory(client, 71594, 20);    // X-Antibody

                        client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize());
                        client.Send(new SystemMessagePacket($"Items for evo on inventory!!"));
                    }
                    break;

                case "spacepack":
                    {
                        var regex = @"^spacepack\s*$";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command.\nType !spacepack"));
                            break;
                        }

                        await AddItemToInventory(client, 5507, 10);     // Inventory Expansion
                        await AddItemToInventory(client, 5508, 10);     // Warehouse Expansion
                        await AddItemToInventory(client, 5004, 10);     // Archive Expansion
                        await AddItemToInventory(client, 5812, 2);      // Digimon Slot

                        client.Send(new LoadInventoryPacket(client.Tamer.Inventory, InventoryTypeEnum.Inventory).Serialize());
                        client.Send(new SystemMessagePacket($"Items for space on inventory!!"));
                    }
                    break;

                case "clon":
                    {
                        var cloneAT = (DigicloneTypeEnum)1;
                        var cloneBL = (DigicloneTypeEnum)2;
                        var cloneCT = (DigicloneTypeEnum)3;
                        var cloneEV = (DigicloneTypeEnum)5;
                        var cloneHP = (DigicloneTypeEnum)7;

                        if (command.Length < 2)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command.\nType !clon type value"));
                            break;
                        }

                        int maxCloneLevel = 15;

                        if (command.Length > 2 && int.TryParse(command[2], out int requestedLevel))
                        {
                            maxCloneLevel = Math.Min(requestedLevel, 15);
                        }

                        async Task IncreaseCloneLevel(DigicloneTypeEnum cloneType, string cloneName)
                        {
                            var currentCloneLevel = client.Partner.Digiclone.GetCurrentLevel(cloneType);

                            while (currentCloneLevel < maxCloneLevel)
                            {
                                var cloneAsset = _assets.CloneValues.FirstOrDefault(x => x.Type == cloneType && currentCloneLevel + 1 >= x.MinLevel && currentCloneLevel + 1 <= x.MaxLevel);

                                if (cloneAsset != null)
                                {
                                    var cloneResult = DigicloneResultEnum.Success;
                                    short value = (short)cloneAsset.MaxValue;

                                    client.Partner.Digiclone.IncreaseCloneLevel(cloneType, value);

                                    client.Send(new DigicloneResultPacket(cloneResult, client.Partner.Digiclone));
                                    client.Send(new UpdateStatusPacket(client.Tamer));

                                    await _sender.Send(new UpdateDigicloneCommand(client.Partner.Digiclone));

                                    currentCloneLevel++;
                                    _logger.Verbose($"New {cloneName} Clon Level: {currentCloneLevel}");
                                }
                                else
                                {
                                    break;
                                }
                            }
                            client.Send(new SystemMessagePacket($"New {cloneName} Clon Level: {currentCloneLevel}"));
                        }

                        switch (command[1].ToLower())
                        {
                            case "at":
                                {
                                    await IncreaseCloneLevel(cloneAT, "AT");
                                }
                                break;

                            case "bl":
                                {
                                    await IncreaseCloneLevel(cloneBL, "BL");
                                }
                                break;

                            case "ct":
                                {
                                    await IncreaseCloneLevel(cloneCT, "CT");
                                }
                                break;

                            case "hp":
                                {
                                    await IncreaseCloneLevel(cloneHP, "HP");
                                }
                                break;

                            case "ev":
                                {
                                    await IncreaseCloneLevel(cloneEV, "EV");
                                }
                                break;

                            default:
                                {
                                    client.Send(new SystemMessagePacket("Unknown command.\nType !clon type value"));
                                }
                                break;
                        }
                    }
                    break;

                // -- BUFF ---------------------------------------

                case "buff":
                    {
                        var regex = @"buff\s(add|remove)\s\d+";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command.\nType !buff (add/remove)"));
                            break;
                        }

                        if (command.Length < 3)
                        {
                            client.Send(new SystemMessagePacket("Invalid command format.\nType !buff (add/remove) (buffID)"));
                            break;
                        }

                        if (!int.TryParse(command[2], out var buffId))
                        {
                            client.Send(new SystemMessagePacket("Invalid item ID format. Please provide a valid number."));
                            break;
                        }

                        var buff = _assets.BuffInfo.FirstOrDefault(x => x.BuffId == buffId);

                        if (buff != null)
                        {
                            var duration = 0;

                            if (command[1].ToLower() == "add")
                            {
                                // Verify if is Tamer Skill
                                if (buff.SkillCode > 0)
                                {
                                    var newCharacterBuff = CharacterBuffModel.Create(buff.BuffId, buff.SkillId, 0, (int)duration);
                                    newCharacterBuff.SetBuffInfo(buff);

                                    client.Tamer.BuffList.Buffs.Add(newCharacterBuff);

                                    client.Send(new UpdateStatusPacket(client.Tamer));
                                    client.Send(new AddBuffPacket(client.Tamer.GeneralHandler, buff, (short)0, duration).Serialize());

                                    await _sender.Send(new UpdateCharacterBuffListCommand(client.Tamer.BuffList));
                                }

                                // Verify if is Digimon Skill
                                if (buff.DigimonSkillCode > 0)
                                {
                                    var newDigimonBuff = DigimonBuffModel.Create(buff.BuffId, buff.SkillId, 0, (int)duration);
                                    newDigimonBuff.SetBuffInfo(buff);

                                    client.Partner.BuffList.Buffs.Add(newDigimonBuff);

                                    client.Send(new UpdateStatusPacket(client.Tamer));
                                    client.Send(new AddBuffPacket(client.Partner.GeneralHandler, buff, (short)0, duration).Serialize());

                                    await _sender.Send(new UpdateDigimonBuffListCommand(client.Partner.BuffList));
                                }

                                client.Send(new SystemMessagePacket($"New buff added"));
                            }
                            else if (command[1].ToLower() == "remove")
                            {
                                // Verify if is Tamer Skill
                                if (buff.SkillCode > 0)
                                {
                                    var characterBuff = client.Tamer.BuffList.ActiveBuffs.FirstOrDefault(x => x.BuffId == buff.BuffId);

                                    if (characterBuff == null)
                                    {
                                        client.Send(new SystemMessagePacket($"CharacterBuff not found"));
                                        return;
                                    }

                                    client.Tamer.BuffList.Buffs.Remove(characterBuff);

                                    client.Send(new UpdateStatusPacket(client.Tamer));
                                    client.Send(new RemoveBuffPacket(client.Tamer.GeneralHandler, buff.BuffId).Serialize());

                                    await _sender.Send(new UpdateCharacterBuffListCommand(client.Tamer.BuffList));
                                }

                                // Verify if is Digimon Skill
                                if (buff.DigimonSkillCode > 0)
                                {
                                    var digimonBuff = client.Partner.BuffList.ActiveBuffs.FirstOrDefault(x => x.BuffId == buff.BuffId);

                                    if (digimonBuff == null)
                                    {
                                        client.Send(new SystemMessagePacket($"DigimonBuff not found"));
                                        return;
                                    }

                                    client.Partner.BuffList.Buffs.Remove(digimonBuff);

                                    client.Send(new UpdateStatusPacket(client.Tamer));
                                    client.Send(new RemoveBuffPacket(client.Partner.GeneralHandler, buff.BuffId).Serialize());

                                    await _sender.Send(new UpdateDigimonBuffListCommand(client.Partner.BuffList));
                                }

                                client.Send(new SystemMessagePacket($"Buff removed !!"));
                            }
                            else
                            {
                                client.Send(new SystemMessagePacket($"Unknown command.\nType !buff (add/remove)"));
                                break;
                            }
                        }
                        else
                        {
                            client.Send(new SystemMessagePacket($"Buff not found !!"));
                        }

                    }
                    break;

                case "title":
                    {
                        var regex = @"title\s(add|remove)\s\d+";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command.\nType !title (add/remove)"));
                            break;
                        }

                        if (command.Length < 3)
                        {
                            client.Send(new SystemMessagePacket("Invalid command format.\nType !title (add/remove) (titleId)"));
                            break;
                        }

                        if (!short.TryParse(command[2], out var titleId))
                        {
                            client.Send(new SystemMessagePacket("Invalid item ID format. Please provide a valid number."));
                            break;
                        }

                        if (command[1].ToLower() == "add")
                        {
                            var newTitle = _assets.AchievementAssets.FirstOrDefault(x => x.QuestId == titleId && x.BuffId > 0);

                            if (newTitle != null)
                            {
                                var buff = _assets.BuffInfo.FirstOrDefault(x => x.BuffId == newTitle.BuffId);

                                var duration = UtilitiesFunctions.RemainingTimeSeconds(0);

                                var newDigimonBuff = DigimonBuffModel.Create(buff.BuffId, buff.SkillId);

                                newDigimonBuff.SetBuffInfo(buff);

                                foreach (var partner in client.Tamer.Digimons.Where(x => x.Id != client.Tamer.Partner.Id))
                                {
                                    var partnernewDigimonBuff = DigimonBuffModel.Create(buff.BuffId, buff.SkillId);

                                    partnernewDigimonBuff.SetBuffInfo(buff);

                                    partner.BuffList.Add(partnernewDigimonBuff);

                                    await _sender.Send(new UpdateDigimonBuffListCommand(partner.BuffList));
                                }

                                client.Partner.BuffList.Add(newDigimonBuff);

                                var mapClient = _mapServer.FindClientByTamerId(client.TamerId);

                                if (mapClient == null)
                                    _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, new AddBuffPacket(client.Partner.GeneralHandler, buff, (short)0, 0).Serialize());
                                else
                                    _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new AddBuffPacket(client.Partner.GeneralHandler, buff, (short)0, 0).Serialize());

                                client.Tamer.UpdateCurrentTitle(titleId);

                                if (mapClient == null)
                                    _dungeonServer.BroadcastForTamerViewsAndSelf(client.TamerId, new UpdateCurrentTitlePacket(client.Tamer.AppearenceHandler, titleId).Serialize());
                                else
                                    _mapServer.BroadcastForTamerViewsAndSelf(client.TamerId, new UpdateCurrentTitlePacket(client.Tamer.AppearenceHandler, titleId).Serialize());

                                client.Send(new UpdateStatusPacket(client.Tamer));

                                await _sender.Send(new UpdateCharacterTitleCommand(client.TamerId, titleId));
                                await _sender.Send(new UpdateDigimonBuffListCommand(client.Partner.BuffList));
                            }
                            else
                            {
                                client.Send(new SystemMessagePacket($"Title {titleId} not found !!"));
                                break;
                            }

                        }
                        else if (command[1].ToLower() == "remove")
                        {
                            client.Send(new SystemMessagePacket($"Remove not implemented, sorry :)"));
                            break;
                        }

                    }
                    break;

                // -- TRANSCENDENCIA -----------------------------

                case "transcend":
                    {
                        var regex = @"^transcend\s+(help)$";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket("Unknown command.\nType !transcend help\n"));
                            break;
                        }

                        if (command[1] == "help")
                        {
                            client.Send(new SystemMessagePacket($"Digimon Comum: Level 5 e 125% size"));
                            client.Send(new SystemMessagePacket($"DigiSpirit precisa apenas de 125% size", ""));
                        }

                    }
                    break;

                // -- INFO ---------------------------------------

                #region INFO

                case "time":
                    {
                        var regex = @"^time\s*$";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command.\nType !time"));
                            break;
                        }

                        client.Send(new SystemMessagePacket($"Hora do Servidor: {DateTime.UtcNow}"));
                    }
                    break;

                case "deckload":
                    {
                        var regex = @"^deckload\s*$";
                        var match = Regex.Match(message, regex, RegexOptions.IgnoreCase);

                        if (!match.Success)
                        {
                            client.Send(new SystemMessagePacket($"Unknown command.\nType !deckload"));
                            break;
                        }

                        var evolution = client.Partner.Evolutions[0];

                        var evoInfo = _assets.EvolutionInfo.FirstOrDefault(x => x.Type == client.Partner.BaseType)?.Lines
                            .FirstOrDefault(x => x.Type == evolution.Type);

                        // --- CREATE DB ----------------------------------------------------------------------------------------

                        var digimonEvolutionInfo = _assets.EvolutionInfo.First(x => x.Type == client.Partner.BaseType);

                        var digimonEvolutions = client.Partner.Evolutions;

                        var encyclopediaExists =
                            client.Tamer.Encyclopedia.Exists(x => x.DigimonEvolutionId == digimonEvolutionInfo.Id);

                        if (!encyclopediaExists)
                        {
                            if (digimonEvolutionInfo != null)
                            {
                                var newEncyclopedia = CharacterEncyclopediaModel.Create(client.TamerId,
                                    digimonEvolutionInfo.Id, client.Partner.Level, client.Partner.Size, 0, 0, 0, 0, 0,
                                    false, false);

                                digimonEvolutions?.ForEach(x =>
                                {
                                    var evolutionLine = digimonEvolutionInfo.Lines.FirstOrDefault(y => y.Type == x.Type);

                                    byte slotLevel = 0;

                                    if (evolutionLine != null)
                                        slotLevel = evolutionLine.SlotLevel;

                                    newEncyclopedia.Evolutions.Add(
                                        CharacterEncyclopediaEvolutionsModel.Create(newEncyclopedia.Id, x.Type, slotLevel,
                                            Convert.ToBoolean(x.Unlocked)));
                                });

                                var encyclopediaAdded =
                                    await _sender.Send(new CreateCharacterEncyclopediaCommand(newEncyclopedia));

                                client.Tamer.Encyclopedia.Add(encyclopediaAdded);

                                _logger.Debug($"Digimon Type {client.Partner.BaseType} encyclopedia created !!");
                            }
                        }
                        else
                        {
                            _logger.Debug($"Encyclopedia already exist !!");
                        }

                        // --- UNLOCK -------------------------------------------------------------------------------------------

                        var encyclopedia = client.Tamer.Encyclopedia.First(x => x.DigimonEvolutionId == evoInfo.EvolutionId);

                        _logger.Debug($"Encyclopedia is: {encyclopedia.Id}, evolution id: {evoInfo.EvolutionId}");

                        if (encyclopedia != null)
                        {
                            var encyclopediaEvolution =
                                encyclopedia.Evolutions.First(x => x.DigimonBaseType == evolution.Type);

                            if (!encyclopediaEvolution.IsUnlocked)
                            {
                                encyclopediaEvolution.Unlock();

                                await _sender.Send(new UpdateCharacterEncyclopediaEvolutionsCommand(encyclopediaEvolution));

                                int LockedEncyclopediaCount = encyclopedia.Evolutions.Count(x => x.IsUnlocked == false);

                                if (LockedEncyclopediaCount <= 0)
                                {
                                    encyclopedia.SetRewardAllowed();
                                    await _sender.Send(new UpdateCharacterEncyclopediaCommand(encyclopedia));
                                }
                            }
                            else
                            {
                                _logger.Debug($"Evolution already unlocked on encyclopedia !!");
                            }
                        }

                        // ------------------------------------------------------------------------------------------------------

                        client.Send(new SystemMessagePacket($"Encyclopedia atualizada !!"));
                    }
                    break;

                #endregion

                // -- HELP ---------------------------------------

                case "help":
                    {
                        var commandsList = new List<string>
                        {
                            "maintenance",
                            "hatch",
                            "notice",
                            "ann",
                            "where",
                            "tamer",
                            "digimon",
                            "currency",
                            "reload",
                            "dc",
                            "ban",
                            "item",
                            "gfstorage",
                            "cashstorage",
                            "hide",
                            "show",
                            "inv",
                            "storage",
                            "godmode",
                            "unlockevos",
                            "openseals",
                            "membership",
                            "fullacc",
                            "clon",
                            "summon",
                            "heal",
                            "stats",
                            "tp",
                            "tpto",
                            "buff",
                        };

                        var packetsToSend = new List<SystemMessagePacket> { new SystemMessagePacket($"SYSTEM COMMANDS:", ""), };

                        int count = 0;

                        foreach (var chunk in commandsList.Chunk(10))
                        {
                            string commandsString = "";
                            chunk.ToList().ForEach(x =>
                            {
                                count++;
                                var space = count > 9 ? "   " : "    ";
                                var name = $"{count}.{space}!{x}";
                                if (x != chunk.Last())
                                {
                                    name += "\n";
                                }
                                commandsString += name;
                            });
                            packetsToSend.Add(new SystemMessagePacket(commandsString, ""));
                        }

                        // Convert packetsToSend to serialized form
                        var serializedPackets = packetsToSend.Select(x => x.Serialize()).ToArray();
                        client.Send(
                            UtilitiesFunctions.GroupPackets(
                                serializedPackets
                            ));
                    }
                    break;

                // -----------------------------------------------

                default:
                    client.Send(new SystemMessagePacket($"Unknown command. Check the available commands on the Admin Portal."));
                    break;
            }
        }

        private async Task SummonMonster(GameClient client, SummonModel? SummonInfo)
        {
            foreach (var mobToAdd in SummonInfo.SummonedMobs)
            {
                var mob = (SummonMobModel)mobToAdd.Clone();

                int radius = 500; // Ajuste este valor para controlar a dispersão dos chefes
                var random = new Random();

                // Gerando valores aleatórios para deslocamento em X e Y
                int xOffset = random.Next(-radius, radius + 1);
                int yOffset = random.Next(-radius, radius + 1);

                // Calculando as novas coordenadas do chefe de raid
                int bossX = client.Tamer.Location.X + xOffset;
                int bossY = client.Tamer.Location.Y + yOffset;

                if (client.DungeonMap)
                {
                    var map = _dungeonServer.Maps.FirstOrDefault(x => x.Clients.Exists(x => x.TamerId == client.TamerId));

                    var mobId = map.SummonMobs.Count + 1;

                    mob.SetId(mobId);

                    if (mob?.Location?.X != 0 && mob?.Location?.Y != 0)
                    {
                        bossX = mob.Location.X;
                        bossY = mob.Location.Y;

                        mob.SetLocation(client.Tamer.Location.MapId, bossX, bossY);
                    }
                    else
                    {
                        mob.SetLocation(client.Tamer.Location.MapId, bossX, bossY);

                    }

                    mob.SetDuration();
                    mob.SetTargetSummonHandle(client.Tamer.GeneralHandler);
                    _dungeonServer.AddSummonMobs(client.Tamer.Location.MapId, mob, client.TamerId);
                }
                else
                {
                    var map = _mapServer.Maps.FirstOrDefault(x => x.Clients.Exists(x => x.TamerId == client.TamerId));
                    var mobId = map.SummonMobs.Count + 1;

                    mob.SetId(mobId);

                    if (mob?.Location?.X != 0 && mob?.Location?.Y != 0)
                    {
                        bossX = mob.Location.X;
                        bossY = mob.Location.Y;

                        mob.SetLocation(client.Tamer.Location.MapId, bossX, bossY);
                    }
                    else
                    {
                        mob.SetLocation(client.Tamer.Location.MapId, bossX, bossY);

                    }

                    mob.SetDuration();
                    mob.SetTargetSummonHandle(client.Tamer.GeneralHandler);
                    _mapServer.AddSummonMobs(client.Tamer.Location.MapId, mob, client.TamerId);

                }
            }
        }

        async Task AddItemToInventory(GameClient client, int itemId, int amount)
        {
            var newItem = new ItemModel();
            newItem.SetItemInfo(_assets.ItemInfo.FirstOrDefault(x => x.ItemId == itemId));
            newItem.ItemId = itemId;
            newItem.Amount = amount;

            if (newItem.IsTemporary)
                newItem.SetRemainingTime((uint)newItem.ItemInfo.UsageTimeMinutes);

            if (client.Tamer.Inventory.AddItem(newItem))
            {
                client.Send(new ReceiveItemPacket(newItem, InventoryTypeEnum.Inventory));
                await _sender.Send(new UpdateItemsCommand(client.Tamer.Inventory));
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }


        // Método auxiliar para enviar uma mensagem de aviso global
        private async Task SendGlobalNotice(string message)
        {
            var packet = new NoticeMessagePacket(message);
            _mapServer.BroadcastGlobal(packet.Serialize());
            _dungeonServer.BroadcastGlobal(packet.Serialize());
        }

        // Método auxiliar para rodar a contagem regressiva da manutenção
        private async Task RunMaintenanceCountdownAsync()
        {
            // Mensagens de contagem regressiva
            var countdownMessages = new[]
            {
                ("Server shutdown for maintenance in 60s", 60000),
                ("Server shutdown for maintenance in 30s", 30000),
                ("Server shutdown for maintenance in 10s", 20000)
            };

            // Enviar mensagens de contagem regressiva
            foreach (var (message, delay) in countdownMessages)
            {
                await Task.Delay(delay);
                await SendGlobalNotice(message);
            }

            // Contagem regressiva final
            for (int i = 5; i >= 0; i--)
            {
                await Task.Delay(1300);
                await SendGlobalNotice($"Server shutdown for maintenance in {i}s");
            }

            // Enviar mensagem de desconexão para todos os servidores
            var disconnectPacket = new DisconnectUserPacket("Server maintenance").Serialize();
            _mapServer.BroadcastGlobal(disconnectPacket);
            _dungeonServer.BroadcastGlobal(disconnectPacket);
        }
    }
}

