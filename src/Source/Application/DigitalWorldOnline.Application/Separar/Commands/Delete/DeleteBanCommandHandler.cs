using DigitalWorldOnline.Commons.Interfaces;
using MediatR;

namespace DigitalWorldOnline.Application.Separar.Commands.Delete
{
    public class DeleteBanCommandHandler : IRequestHandler<DeleteBanCommand>
    {
        private readonly IAccountCommandsRepository _repository;

        public DeleteBanCommandHandler(IAccountCommandsRepository repository)
        {
            _repository = repository;
        }

        public async Task<Unit> Handle(DeleteBanCommand request, CancellationToken cancellationToken)
        {
            await _repository.DeleteBanAsync(request.Id);

            return Unit.Value;
        }
    }
}
