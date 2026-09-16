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
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status415UnsupportedMediaType, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout, "application/problem+json")]
    public async Task<ActionResult<IngestionResult>> StopSearches([FromBody] LocationMonthRequest? request) =>
        Ok(await stopSearches.IngestAsync(request, HttpContext.RequestAborted));
}
