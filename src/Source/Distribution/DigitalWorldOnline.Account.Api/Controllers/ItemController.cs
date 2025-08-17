using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using DigitalWorldOnline.Api.Dtos.In;
using DigitalWorldOnline.Application.Separar.Commands.Update;
using DigitalWorldOnline.Application.Separar.Queries;
using DigitalWorldOnline.Commons.Models.Asset;
using DigitalWorldOnline.Commons.Models.Base;
using DigitalWorldOnline.Commons.Models.Character;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Serilog;

namespace DigitalWorldOnline.Api.Controllers
{
    [ApiController]
    [Route("v1/[controller]")]
    public class ItemController : ControllerBase
    {
        private readonly ISender _sender;
        private readonly Serilog.ILogger _logger;   // <- Serilog explicitamente
        private readonly IMapper _mapper;
        private readonly IMemoryCache _cache;

        public ItemController(
            ISender sender,
            Serilog.ILogger logger,                 // <- Serilog explicitamente
            IMapper mapper,
            IMemoryCache cache)
        {
            _sender = sender;
            _logger = logger;
            _mapper = mapper;
            _cache = cache;
        }

        /// <summary>
        /// Adiciona um item ao Cash Warehouse do utilizador.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> ProcessItem([FromBody] ProcessItemRequest request, CancellationToken ct)
        {
            try
            {
                if (request is null)
                    return BadRequest(new { Message = "Body required." });
                if (request.AccountId <= 0)
                    return BadRequest(new { Message = "Invalid AccountId." });
                if (request.ItemId <= 0)
                    return BadRequest(new { Message = "Invalid ItemId." });
                if (request.Amount <= 0)
                    return BadRequest(new { Message = "Amount must be greater than 0." });

                // Cache de assets (lista completa); depois filtramos por ItemId
                var itemAssets = await _cache.GetOrCreateAsync("ItemAssets_All", async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
                    var assetsDto = await _sender.Send(new ItemAssetsQuery(), ct);
                    return _mapper.Map<IList<ItemAssetModel>>(assetsDto);
                });

                var assetInfo = itemAssets?.FirstOrDefault(x => x.ItemId == request.ItemId);
                if (assetInfo == null)
                {
                    _logger.Warning("ProcessItem: ItemId={ItemId} not found in assets.", request.ItemId);
                    return NotFound(new { Message = $"ItemId {request.ItemId} not found." });
                }

                // Cria o item a partir do asset
                var newItem = new ItemModel();
                newItem.SetItemInfo(assetInfo);
                newItem.ItemId = request.ItemId;
                newItem.Amount = request.Amount;

                if (newItem.IsTemporary)
                {
                    var minutes = (uint)(newItem.ItemInfo?.UsageTimeMinutes ?? 0);
                    if (minutes > 0)
                        newItem.SetRemainingTime(minutes);
                }

                // Busca conta e a lista CashWarehouse
                var accountDto = await _sender.Send(new AccountByIdQuery(request.AccountId), ct);
                if (accountDto == null)
                {
                    _logger.Warning("ProcessItem: AccountId={AccountId} not found.", request.AccountId);
                    return NotFound(new { Message = $"Account {request.AccountId} not found." });
                }

                var cashWarehouseDto = accountDto.ItemList?.FirstOrDefault(x => x.Type == DigitalWorldOnline.Commons.Enums.ItemListEnum.CashWarehouse);
                if (cashWarehouseDto == null)
                {
                    _logger.Warning("ProcessItem: CashWarehouse not found for AccountId={AccountId}.", request.AccountId);
                    return Problem(statusCode: 500, detail: "CashWarehouse not found for account.");
                }

                var cashWarehouse = _mapper.Map<ItemListModel>(cashWarehouseDto);
                if (cashWarehouse == null)
                {
                    _logger.Error("ProcessItem: Failed to map CashWarehouse for AccountId={AccountId}.", request.AccountId);
                    return Problem(statusCode: 500, detail: "Mapping error for CashWarehouse.");
                }

                // Adiciona item (stack/capacidade devem ser tratados dentro de AddItem)
                var clone = (ItemModel)newItem.Clone();
                var added = cashWarehouse.AddItem(clone);
                if (!added)
                {
                    _logger.Warning("ProcessItem: AddItem returned false (AccountId={AccountId}, ItemId={ItemId}).",
                        request.AccountId, request.ItemId);
                    return Conflict(new { Message = "Unable to add item (inventory full or invalid stack)." });
                }

                // Persiste
                await _sender.Send(new UpdateItemsCommand(cashWarehouse), ct);

                _logger.Information("ProcessItem: OK AccountId={AccountId} ItemId={ItemId} Amount={Amount}",
                    request.AccountId, request.ItemId, request.Amount);

                return Ok(new
                {
                    request.ItemId,
                    request.Amount,
                    Message = "Item processed successfully"
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "ProcessItem: Exception for AccountId={AccountId} ItemId={ItemId}",
                    request?.AccountId, request?.ItemId);
                return Problem(statusCode: 500, detail: "Internal error while processing item.");
            }
        }
    }
}
