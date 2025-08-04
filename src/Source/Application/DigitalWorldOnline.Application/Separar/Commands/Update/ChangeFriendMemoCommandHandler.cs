
using DigitalWorldOnline.Commons.DTOs.Character;
using DigitalWorldOnline.Commons.DTOs.Digimon;
using DigitalWorldOnline.Commons.Interfaces;
using MediatR;

namespace DigitalWorldOnline.Application.Separar.Commands.Update
{
    public class ChangeFriendMemoCommandHandler : IRequestHandler<ChangeFriendMemoCommand, CharacterFriendDTO>
    {
        private readonly ICharacterCommandsRepository _repository;

        public ChangeFriendMemoCommandHandler(ICharacterCommandsRepository repository)
        {
            _repository = repository;
        }

        public async Task<CharacterFriendDTO> Handle(ChangeFriendMemoCommand request, CancellationToken cancellationToken)
        {
            return await _repository.ChangeFriendMemoAsync(request.Id, request.NewMemo);
        }
    }
}