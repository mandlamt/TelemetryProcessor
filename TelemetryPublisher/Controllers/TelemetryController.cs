using Microsoft.AspNetCore.Mvc;
using TelemetryPublisher.Services;

namespace TelemetryPublisher.Controllers;

[ApiController]
[Route("api/telemetry")]
public class TelemetryController : ControllerBase
{
    private readonly ITelemetryStore _store;
    private readonly ILogger<TelemetryController> _logger;

    public TelemetryController(ITelemetryStore store, ILogger<TelemetryController> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Returns the oldest unconsumed reading, preferring in-memory data and
    /// falling back to spilled database records. Returns 204 No Content if
    /// nothing is available.
    /// </summary>
    [HttpGet("next")]
    public async Task<IActionResult> GetNext(CancellationToken ct)
    {
        var reading = await _store.GetNextAsync(ct);

        if (reading is null)
        {
            return NoContent();
        }

        return Ok(reading);
    }

    /// <summary>Returns current queue depth, total generated, and total spilled counts.</summary>
    [HttpGet("stats")]
    public IActionResult GetStats()
    {
        var stats = _store.GetStats();
        return Ok(stats);
    }
}
