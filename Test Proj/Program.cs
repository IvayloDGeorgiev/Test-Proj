using Test_Proj.Options;
using Test_Proj.Clients.PoliceApi;
using Test_Proj.Export;
using Test_Proj.Services.Forces;
using Test_Proj.Errors;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddDevelopmentLocalConfiguration(builder.Environment);
builder.Services.AddIngestionOptions(builder.Configuration, builder.Environment);
builder.Services.AddPoliceApiClient();
builder.Services.AddCsvExport();
builder.Services.AddScoped<IForcesIngestionService, ForcesIngestionService>();
builder.Services.AddScoped<Test_Proj.Services.Crimes.ICrimesIngestionService, Test_Proj.Services.Crimes.CrimesIngestionService>();

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();
app.UseMiddleware<IngestionErrorMiddleware>();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
