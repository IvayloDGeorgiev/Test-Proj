using Test_Proj.Options;
using Test_Proj.Clients.PoliceApi;
using Test_Proj.Export;
using Test_Proj.Services.Forces;
using Test_Proj.Errors;
using Test_Proj.Persistence;
using System.Reflection.Metadata.Ecma335;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddDevelopmentLocalConfiguration(builder.Environment);
builder.Services.AddIngestionOptions(builder.Configuration, builder.Environment);
builder.Services.AddPoliceApiClient();
builder.Services.AddCsvExport();
builder.Services.AddPolicePersistence(builder.Configuration);
builder.Services.AddScoped<Test_Proj.Services.StopSearches.IStopSearchesIngestionService, Test_Proj.Services.StopSearches.StopSearchesIngestionService>();
builder.Services.AddScoped<IForcesIngestionService, ForcesIngestionService>();
builder.Services.AddScoped<Test_Proj.Services.Crimes.ICrimesIngestionService, Test_Proj.Services.Crimes.CrimesIngestionService>();

// Add services to the container.

builder.Services.AddControllers(options => options.Filters.Add<IngestionResultFilter>())
    .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = context =>
    {
        var problem = new Microsoft.AspNetCore.Mvc.ValidationProblemDetails(
            new Dictionary<string, string[]> { ["request"] = ["Supply a valid JSON request."] })
        { Status = 400, Title = "Request validation failed.", Type = "about:blank" };
        problem.Extensions["code"] = "validation_failed";
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        var result = new Microsoft.AspNetCore.Mvc.BadRequestObjectResult(problem);
        result.ContentTypes.Add("application/problem+json");
        return result;
    });
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}
app.UseMiddleware<IngestionErrorMiddleware>();
app.UseMiddleware<SyncOriginMiddleware>();
app.UseMiddleware<IngestionLimitsMiddleware>();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'";
    if (context.Request.Path.StartsWithSegments("/api/data")) context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthorization();

app.MapControllers();

app.Run();

// Expose the existing entry point to the isolated integration test host.
public partial class Program { }
