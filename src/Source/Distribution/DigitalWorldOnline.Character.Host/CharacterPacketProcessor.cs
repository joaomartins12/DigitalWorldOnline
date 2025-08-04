using AutoMapper;
using DigitalWorldOnline.Application.Separar.Commands.Create;
using DigitalWorldOnline.Application.Separar.Commands.Delete;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Application.Separar.Queries;
using DigitalWorldOnline.Commons.Entities;
using DigitalWorldOnline.Commons.Enums;
using DigitalWorldOnline.Commons.Enums.Character;
using DigitalWorldOnline.Commons.Enums.PacketProcessor;
using DigitalWorldOnline.Commons.Extensions;
using DigitalWorldOnline.Commons.Interfaces;
using DigitalWorldOnline.Commons.Models.Account;
using DigitalWorldOnline.Commons.Models.Asset;
using DigitalWorldOnline.Commons.Models.Character;
using DigitalWorldOnline.Commons.Models.Digimon;
using DigitalWorldOnline.Commons.Packets.CharacterServer;
using DigitalWorldOnline.Commons.Utils;
using MediatR;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace DigitalWorldOnline.Character
{
    public sealed class CharacterPacketProcessor : IProcessor, IDisposable
    {
        private readonly IConfiguration _configuration;
        private readonly ISender _sender;
        private readonly ILogger _logger;
        private readonly IMapper _mapper;

        private const string GameServerAddress = "GameServer:Address";
        private const string GamerServerPublic = "GameServer:PublicAddress";
        private const string GameServerPort = "GameServer:Port";
        private const int HandshakeDegree = 32321;
        private const int HandshakeStampDegree = 65535;

        public CharacterPacketProcessor(ILogger logger,
            ISender sender,
            IConfiguration configuration,
            IMapper mapper)
        {
            _configuration = configuration;
            _sender = sender;
            _logger = logger;
            _mapper = mapper;
        }

        /// <summary>
        /// Process the arrived TCP packet, sent from the game client
        /// </summary>
        /// <param name="client">The game client whos sent the packet</param>
        /// <param name="data">The packet bytes array</param>
        public async Task ProcessPacketAsync(GameClient client, byte[] data)
        {
            var packet = new CharacterPacketReader(data);

            _logger.Debug("Received packet type {Type} from {Address}", packet.Enum, client.ClientAddress);

            switch (packet.Enum)
            {
                case CharacterServerPacketEnum.Connection:
                    {
                        _logger.Debug("Reading packet parameters...");
                        var kind = packet.ReadByte();

                        var handshakeTimestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        var handshake = (short)(client.Handshake ^ HandshakeDegree);

                        client.Send(new ConnectionPacket(handshake, handshakeTimestamp).Serialize());
                    }
                    break;

                case CharacterServerPacketEnum.KeepConnection:
                    // No action needed for KeepConnection
                    break;

                case CharacterServerPacketEnum.RequestCharacters:
                    {
                        packet.Seek(8);
                        var accountId = packet.ReadUInt();
                        _logger.Debug($"Getting account {accountId} character list...");

                        var characters = _mapper.Map<List<CharacterModel>>(await _sender.Send(new CharactersByAccountIdQuery(accountId)));

                        characters.ForEach(character =>
                        {
                            if (character.Partner.CurrentType != character.Partner.BaseType)
                            {
                                _logger.Debug("Updating partner's current type...");
                                character.Partner.UpdateCurrentType(character.Partner.BaseType);
                                _sender.Send(new UpdatePartnerCurrentTypeCommand(character.Partner));
                            }
                        });

                        client.Send(new CharacterListPacket(characters));
                        client.SetAccountId(accountId);
                    }
                    break;

                case CharacterServerPacketEnum.CreateCharacter:
                    {
                        var position = packet.ReadByte();
                        var tamerModel = packet.ReadInt();
                        var tamerName = packet.ReadZString();
                        packet.Seek(42);
                        var digimonModel = packet.ReadInt();
                        var digimonName = packet.ReadZString();

                        var account = _mapper.Map<AccountModel>(await _sender.Send(new AccountByIdQuery(client.AccountId)));

                        _logger.Debug("Creating character...");
                        var character = CharacterModel.Create(
                            client.AccountId,
                            tamerName,
                            tamerModel,
                            position,
                            account.LastPlayedServer);

                        _logger.Debug("Creating digimon...");
                        var digimon = DigimonModel.Create(
                            digimonName,
                            digimonModel,
                            digimonModel,
                            DigimonHatchGradeEnum.Perfect,
                            UtilitiesFunctions.RandomShort(12000, 12000),
                            0);

                        character.AddDigimon(digimon);

                        var handshakeTimestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        var handshake = (short)(handshakeTimestamp & HandshakeStampDegree);
                        client.Send(new CharacterCreatedPacket(character, handshake));

                        _logger.Debug("Getting tamer status information...");
                        character.SetBaseStatus(
                            _mapper.Map<CharacterBaseStatusAssetModel>(
                                await _sender.Send(new TamerBaseStatusQuery(character.Model))
                            ));

                        character.SetLevelStatus(
                            _mapper.Map<CharacterLevelStatusAssetModel>(
                                await _sender.Send(new TamerLevelStatusQuery(character.Model, character.Level))
                            ));

                        character.Partner.SetBaseInfo(
                            _mapper.Map<DigimonBaseInfoAssetModel>(
                                await _sender.Send(new DigimonBaseInfoQuery(character.Partner.CurrentType))
                            ));

                        _logger.Debug($"Registering tamer and digimon for account {account.Username}...");
                        character.Partner.AddEvolutions(await _sender.Send(new DigimonEvolutionAssetsByTypeQuery(digimonModel)));
                        await _sender.Send(new CreateCharacterCommand(character));
                    }
                    break;

                case CharacterServerPacketEnum.CheckNameDuplicity:
                    {
                        var tamerName = packet.ReadString();
                        var account = _mapper.Map<AccountModel>(await _sender.Send(new AccountByIdQuery(client.AccountId)));

                        tamerName = tamerName.ModeratorPrefix(account.AccessLevel);

                        _logger.Debug("Checking tamer name duplicity...");
                        var availableName = await _sender.Send(new CharacterByNameQuery(tamerName)) == null;
                        client.Send(new AvailableNamePacket(availableName).Serialize());
                    }
                    break;

                case CharacterServerPacketEnum.DeleteCharacter:
                    {
                        var position = packet.ReadByte();
                        packet.Skip(3);
                        var validation = packet.ReadString();

                        var account = _mapper.Map<AccountModel>(await _sender.Send(new AccountByIdQuery(client.AccountId)));

                        if (account.CharacterDeleteValidation(validation))
                        {
                            _logger.Debug("Deleting character...");
                            var deletedCharacter = await _sender.Send(new DeleteCharacterCommand(client.AccountId, position));
                            client.Send(new CharacterDeletedPacket(deletedCharacter).Serialize());
                        }
                        else
                        {
                            _logger.Debug($"Validation fail for deleting character in account {account.Username}.");
                            client.Send(new CharacterDeletedPacket(DeleteCharacterResultEnum.ValidationFail).Serialize());
                        }
                    }
                    break;

                case CharacterServerPacketEnum.GetCharacterPosition:
                    {
                        var position = packet.ReadByte();
                        _logger.Information($"Searching character...");
                        var character = _mapper.Map<CharacterModel>(await _sender.Send(new CharacterByAccountIdAndPositionQuery(client.AccountId, position)));

                        while (character == null)
                        {
                            await Task.Delay(1500);
                            _logger.Debug("Searching character again...");
                            character = _mapper.Map<CharacterModel>(await _sender.Send(new CharacterByAccountIdAndPositionQuery(client.AccountId, position)));
                        }

                        _logger.Information($"Updating access information for account {client.AccountId}.");
                        await _sender.Send(new UpdateLastPlayedCharacterCommand(client.AccountId, character.Id));
                        await _sender.Send(new UpdateCharacterChannelCommand(character.Id));
                        await _sender.Send(new UpdateAccountWelcomeFlagCommand(character.AccountId));

                        _logger.Information($"Sending selected server info...");
                        client.Send(new ConnectGameServerInfoPacket(
                            _configuration[GameServerAddress],
                            _configuration[GameServerPort],
                            character.Location.MapId).Serialize());
                    }
                    break;

                case CharacterServerPacketEnum.ConnectGameServer:
                    {
                        _logger.Information("Sending answer to connect to game server...");
                        client.Send(new ConnectGameServerPacket().Serialize());
                    }
                    break;

                default:
                    _logger.Warning("Unknown packet. Type: {Type} Length: {Length}.", packet.Enum, packet.Length);
                    break;
            }
        }


        /// <summary>
        /// Shortcut for debug logging with client and packet info.
        /// </summary>
        /// <param name="message">The message to log</param>
        private void DebugLog(string message)
        {
            _logger?.Debug($"{message}");
        }

        /// <summary>
        /// Shortcut for info logging.
        /// </summary>
        /// <param name="message">The message to log</param>
        private void InfoLog(string message)
        {
            _logger?.Information($"{message}");
        }

        /// <summary>
        /// Disposes the entire object.
        /// </summary>
        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}