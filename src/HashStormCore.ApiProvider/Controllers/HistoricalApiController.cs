using HashStormCore.ApiProvider.Services;
using Microsoft.AspNetCore.Mvc;

namespace HashStormCore.ApiProvider.Controllers;

[ApiController]
[Route("historical")]
public class HistoricalApiController : ControllerBase
{
    public HistoricalApiController(PostgresHistoricalReadService historicalReadService)
    {
        this.historicalReadService = historicalReadService;
    }

    private readonly PostgresHistoricalReadService historicalReadService;

    [HttpGet("pools/{poolId}/stats/latest")]
    public async Task<IActionResult> GetLatestPoolStats(string poolId, CancellationToken ct)
    {
        var result = await historicalReadService.GetLatestPoolStatsAsync(poolId, ct);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpGet("pools/{poolId}/share-events")]
    public async Task<IActionResult> GetShareEvents(string poolId, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit, CancellationToken ct)
    {
        return Ok(await historicalReadService.GetShareEventsAsync(poolId, from, to, limit <= 0 ? 100 : limit, ct));
    }
}
