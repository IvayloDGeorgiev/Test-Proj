using Microsoft.AspNetCore.Mvc;
using Test_Proj.Contracts.Requests;
using Test_Proj.Contracts.Responses;
using Test_Proj.Services.StopSearches;

namespace Test_Proj.Controllers;

[ApiController]
[Route("api/ingestion/stop-searches")]
public sealed class StopSearchesIngestionController(IStopSearchesIngestionService stopSearches) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<IngestionResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<IngestionResult>> StopSearches([FromBody] LocationMonthRequest? request) =>
        Ok(await stopSearches.IngestAsync(request, HttpContext.RequestAborted));
}
