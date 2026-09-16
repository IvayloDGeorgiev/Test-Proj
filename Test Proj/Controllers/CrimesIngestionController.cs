using Microsoft.AspNetCore.Mvc;
using Test_Proj.Contracts.Requests;
using Test_Proj.Contracts.Responses;
using Test_Proj.Services.Crimes;

namespace Test_Proj.Controllers;

[ApiController]
[Route("api/ingestion/crimes")]
public sealed class CrimesIngestionController(ICrimesIngestionService crimes) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<IngestionResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<IngestionResult>> Crimes([FromBody] LocationMonthRequest? request) =>
        Ok(await crimes.IngestAsync(request, HttpContext.RequestAborted));
}
