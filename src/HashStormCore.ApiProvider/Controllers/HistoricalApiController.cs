using HashStormCore.ApiProvider.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace HashStormCore.ApiProvider.Controllers;

[ApiController]
[Route("historical")]
public class HistoricalApiController : ControllerBase
{
    public HistoricalApiController(PostgresHistoricalReadService historicalReadService, SanitizedPoolConfigService poolConfigService,
        ILogger<HistoricalApiController> logger)
    {
        this.historicalReadService = historicalReadService;
        this.poolConfigService = poolConfigService;
        this.logger = logger;
    }

    private readonly PostgresHistoricalReadService historicalReadService;
    private readonly SanitizedPoolConfigService poolConfigService;
    private readonly ILogger<HistoricalApiController> logger;

    [HttpGet("pools/{poolId}/stats/latest")]
    public Task<IActionResult> GetLatestPoolStats(string poolId, CancellationToken ct) =>
        ExecuteNullableReadAsync("latest pool stats", () => historicalReadService.GetLatestPoolStatsAsync(poolId, ct));

    [HttpGet("pools/{poolId}/share-events")]
    public Task<IActionResult> GetShareEvents(string poolId, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit, CancellationToken ct) =>
        ExecutePagedReadAsync("share events", 0, () => historicalReadService.GetShareEventsAsync(poolId, from, to, limit <= 0 ? 100 : limit, ct));

    [HttpGet("pools/{poolId}/blocks")]
    public Task<IActionResult> GetBlocks(string poolId, [FromQuery] string status, [FromQuery] string type,
        [FromQuery] string miner, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = PostgresHistoricalReadService.DefaultPageLimit, [FromQuery] int offset = 0,
        CancellationToken ct = default) =>
        ExecutePagedReadAsync("blocks", offset, () => historicalReadService.GetBlocksAsync(poolId, status, type, miner, from, to, limit, offset, ct));

    [HttpGet("pools/{poolId}/payments")]
    public Task<IActionResult> GetPayments(string poolId, [FromQuery] string address, [FromQuery] string coin,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = PostgresHistoricalReadService.DefaultPageLimit, [FromQuery] int offset = 0,
        CancellationToken ct = default) =>
        ExecutePagedReadAsync("payments", offset, () => historicalReadService.GetPaymentsAsync(poolId, address, coin, from, to, limit, offset, ct));

    [HttpGet("pools/{poolId}/miners/{miner}/payments")]
    public Task<IActionResult> GetMinerPayments(string poolId, string miner, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = PostgresHistoricalReadService.DefaultPageLimit, [FromQuery] int offset = 0,
        CancellationToken ct = default) =>
        ExecutePagedReadAsync("miner payments", offset, () => historicalReadService.GetMinerPaymentsAsync(poolId, miner, from, to, limit, offset, ct));

    [HttpGet("pools/{poolId}/miners/{miner}/balance")]
    public Task<IActionResult> GetMinerBalance(string poolId, string miner, CancellationToken ct) =>
        ExecuteNullableReadAsync("miner balance", () => historicalReadService.GetMinerBalanceAsync(poolId, miner, ct));

    [HttpGet("pools/{poolId}/miners/{miner}/balance-changes")]
    public Task<IActionResult> GetMinerBalanceChanges(string poolId, string miner, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] string usage, [FromQuery] string tag,
        [FromQuery] int limit = PostgresHistoricalReadService.DefaultPageLimit, [FromQuery] int offset = 0,
        CancellationToken ct = default) =>
        ExecutePagedReadAsync("miner balance changes", offset, () => historicalReadService.GetMinerBalanceChangesAsync(poolId, miner, from, to, usage, tag, limit, offset, ct));

    [HttpGet("pools/{poolId}/stats")]
    public Task<IActionResult> GetPoolStats(string poolId, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = PostgresHistoricalReadService.DefaultPageLimit, [FromQuery] int offset = 0,
        CancellationToken ct = default) =>
        ExecutePagedReadAsync("pool stats", offset, () => historicalReadService.GetPoolStatsAsync(poolId, from, to, limit, offset, ct));

    [HttpGet("pools/{poolId}/miners/{miner}/stats")]
    public Task<IActionResult> GetMinerStats(string poolId, string miner, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = PostgresHistoricalReadService.DefaultPageLimit, [FromQuery] int offset = 0,
        CancellationToken ct = default) =>
        ExecutePagedReadAsync("miner stats", offset, () => historicalReadService.GetMinerStatsAsync(poolId, miner, from, to, limit, offset, ct));

    [HttpGet("pools/{poolId}/miners/{miner}/worker-stats")]
    public Task<IActionResult> GetWorkerStats(string poolId, string miner, [FromQuery] string worker,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int limit = PostgresHistoricalReadService.DefaultPageLimit, [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        if(string.IsNullOrWhiteSpace(worker))
            return Task.FromResult<IActionResult>(BadRequest(new { error = "worker is required" }));

        return ExecutePagedReadAsync("worker stats", offset,
            () => historicalReadService.GetWorkerStatsAsync(poolId, miner, worker, from, to, limit, offset, ct));
    }

    [HttpGet("pools")]
    public IActionResult GetPools(CancellationToken ct) =>
        ExecuteRead("sanitized pool list", ct, () => poolConfigService.GetPools());

    [HttpGet("pools/{poolId}/info")]
    public IActionResult GetPoolInfo(string poolId, CancellationToken ct) =>
        ExecuteNullableRead("sanitized pool info", ct, () => poolConfigService.GetPool(poolId));

    private async Task<IActionResult> ExecutePagedReadAsync<T>(string operation, int offset, Func<Task<IReadOnlyList<T>>> read)
    {
        if(offset < 0)
            return BadRequest(new { error = "offset must be greater than or equal to 0" });

        try
        {
            return Ok(await read());
        }
        catch(OperationCanceledException)
        {
            throw;
        }
        catch(Exception ex)
        {
            logger.LogError(ex, "Historical {Operation} read failed", operation);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Historical data is unavailable" });
        }
    }

    private async Task<IActionResult> ExecuteNullableReadAsync<T>(string operation, Func<Task<T>> read) where T : class
    {
        try
        {
            var result = await read();
            return result == null ? NotFound() : Ok(result);
        }
        catch(OperationCanceledException)
        {
            throw;
        }
        catch(Exception ex)
        {
            logger.LogError(ex, "Historical {Operation} read failed", operation);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Historical data is unavailable" });
        }
    }

    private IActionResult ExecuteRead<T>(string operation, CancellationToken ct, Func<T> read)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return Ok(read());
        }
        catch(OperationCanceledException)
        {
            throw;
        }
        catch(Exception ex)
        {
            logger.LogError(ex, "Historical {Operation} read failed", operation);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Historical data is unavailable" });
        }
    }

    private IActionResult ExecuteNullableRead<T>(string operation, CancellationToken ct, Func<T> read) where T : class
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var result = read();
            return result == null ? NotFound() : Ok(result);
        }
        catch(OperationCanceledException)
        {
            throw;
        }
        catch(Exception ex)
        {
            logger.LogError(ex, "Historical {Operation} read failed", operation);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Historical data is unavailable" });
        }
    }
}
