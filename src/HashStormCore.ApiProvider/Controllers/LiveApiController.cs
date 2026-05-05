using HashStormCore.ApiProvider.Services;
using Microsoft.AspNetCore.Mvc;

namespace HashStormCore.ApiProvider.Controllers;

[ApiController]
[Route("live")]
public class LiveApiController : ControllerBase
{
    public LiveApiController(RedisLiveReadService liveReadService)
    {
        this.liveReadService = liveReadService;
    }

    private readonly RedisLiveReadService liveReadService;

    [HttpGet("pools/{poolId}/summary")]
    public async Task<IActionResult> GetPoolSummary(string poolId)
    {
        var result = await liveReadService.GetPoolSummaryAsync(poolId);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpGet("pools/{poolId}/top-miners")]
    public async Task<IActionResult> GetTopMiners(string poolId, [FromQuery] int count = 100)
    {
        return Ok(await liveReadService.GetTopMinersAsync(poolId, Math.Clamp(count, 1, 500)));
    }

    [HttpGet("pools/{poolId}/miners/{miner}")]
    public async Task<IActionResult> GetMiner(string poolId, string miner)
    {
        var result = await liveReadService.GetMinerSummaryAsync(poolId, miner);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpGet("pools/{poolId}/miners/{miner}/workers")]
    public async Task<IActionResult> GetMinerWorkers(string poolId, string miner)
    {
        return Ok(await liveReadService.GetMinerWorkersAsync(poolId, miner));
    }

    [HttpGet("pools/{poolId}/status")]
    public async Task<IActionResult> GetPoolStatus(string poolId)
    {
        var result = await liveReadService.GetPoolStatusAsync(poolId);
        return result == null ? NotFound() : Ok(result);
    }
}
