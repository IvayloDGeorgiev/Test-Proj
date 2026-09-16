using Microsoft.AspNetCore.Mvc;
using Test_Proj.Contracts.Requests;
using Test_Proj.Services.Sync;

namespace Test_Proj.Controllers;

[ApiController]
[Route("api/data")]
[ProducesResponseType<ValidationProblemDetails>(400, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(413, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(500, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(503, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(504, "application/problem+json")]
public sealed class DataController(PoliceSyncService service) : ControllerBase
{
    [HttpGet("{dataset}")]
    [ProducesResponseType<PersistedPage>(200)]
    public Task<PersistedPage> Get(string dataset, CancellationToken cancellationToken,
        [FromQuery] double? latitude = null, [FromQuery] double? longitude = null,
        [FromQuery] string? month = null, [FromQuery] int offset = 0, [FromQuery] int limit = 50) =>
        service.ReadAsync(dataset, new(latitude, longitude, month), offset, limit, cancellationToken);
}

[ApiController]
[Route("api/sync")]
[ProducesResponseType<SyncResult>(200)]
[ProducesResponseType<ValidationProblemDetails>(400, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(403, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(409, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(413, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(415, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(500, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(502, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(503, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(504, "application/problem+json")]
public sealed class SyncController(PoliceSyncService service) : ControllerBase
{
    [HttpPost("forces")]
    public Task<SyncResult> Forces(CancellationToken cancellationToken) => service.SyncAsync("forces", null, cancellationToken);

    [HttpPost("crimes")]
    public Task<SyncResult> Crimes([FromBody] LocationMonthRequest request, CancellationToken cancellationToken) =>
        service.SyncAsync("crimes", request, cancellationToken);

    [HttpPost("stop-searches")]
    public Task<SyncResult> Stops([FromBody] LocationMonthRequest request, CancellationToken cancellationToken) =>
        service.SyncAsync("stop-searches", request, cancellationToken);
}
