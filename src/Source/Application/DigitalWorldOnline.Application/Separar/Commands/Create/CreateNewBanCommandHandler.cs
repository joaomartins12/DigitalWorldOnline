using DigitalWorldOnline.Commons.DTOs.Account;
using DigitalWorldOnline.Commons.DTOs.Character;
using DigitalWorldOnline.Commons.DTOs.Digimon;
using DigitalWorldOnline.Commons.Interfaces;
using MediatR;

namespace DigitalWorldOnline.Application.Separar.Commands.Create
{
    public class CreateNewBanCommandHandler : IRequestHandler<CreateNewBanCommand, AccountBlockDTO>
    {
        private readonly ICharacterCommandsRepository _repository;

        public CreateNewBanCommandHandler(ICharacterCommandsRepository repository)
        {
            _repository = repository;
        }

        public async Task<AccountBlockDTO> Handle(CreateNewBanCommand request, CancellationToken cancellationToken)
        {
            return await _repository.AddBanAsync(request.Ban);
        }
    }
}