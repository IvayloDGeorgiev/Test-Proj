using Microsoft.AspNetCore.Mvc;
using Test_Proj.Contracts.Responses;
using Test_Proj.Services.Forces;

namespace Test_Proj.Controllers;

[ApiController]
[Route("api/ingestion")]
public sealed class IngestionController(IForcesIngestionService forces) : ControllerBase
{
    [HttpPost("forces")]
    [ProducesResponseType<IngestionResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<IngestionResult>> Forces() =>
        Ok(await forces.IngestAsync(HttpContext.RequestAborted));
}
