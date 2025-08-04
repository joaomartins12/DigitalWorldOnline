using DigitalWorldOnline.Commons.DTOs.Config;
using DigitalWorldOnline.Commons.Interfaces;
using MediatR;

namespace DigitalWorldOnline.Application.Separar.Queries
{
    public class GlobalDropsConfigQueryHandler : IRequestHandler<GlobalDropsConfigQuery, List<GlobalDropsConfigDTO>>
    {
        private readonly IServerQueriesRepository _repository;

        public GlobalDropsConfigQueryHandler(IServerQueriesRepository repository)
        {
            _repository = repository;
        }

        public async Task<List<GlobalDropsConfigDTO>> Handle(GlobalDropsConfigQuery request, CancellationToken cancellationToken)
        {
            return await _repository.GetGlobalDropsConfigsAsync();
        }
    }
}