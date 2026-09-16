using Test_Proj.Clients.PoliceApi;
using Test_Proj.Contracts.Requests;
using Test_Proj.Contracts.Responses;
using Test_Proj.Errors;
using Test_Proj.Export;
using Test_Proj.Validation;

namespace Test_Proj.Services.Crimes;

public interface ICrimesIngestionService
{
    Task<IngestionResult> IngestAsync(LocationMonthRequest? request, CancellationToken cancellationToken);
}

public sealed class CrimesIngestionService(IPoliceApiClient client, ICsvExporter exporter,
    OperationLease admission, TimeProvider clock) : ICrimesIngestionService
{
    public async Task<IngestionResult> IngestAsync(LocationMonthRequest? request, CancellationToken cancellationToken)
    {
        var query = request?.Validate() ?? LocationMonth.Create(null, null, null);
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = admission.Acquire();
        var crimes = await client.GetCrimesAsync(query, cancellationToken);
        if (crimes is null) throw new PoliceApiException(PoliceApiFailure.InvalidPayload);
        var rows = new List<IReadOnlyList<CsvCell>>(crimes.Count);
        foreach (var crime in crimes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (crime is null || !crime.IsValid(query.Month))
                throw new PoliceApiException(PoliceApiFailure.InvalidPayload);
            rows.Add([
                CsvCell.Integer(crime.Id), CsvCell.Text(crime.PersistentId), CsvCell.Text(crime.Category),
                CsvCell.Text(crime.Month), CsvCell.Number(CrimeLocationDto.Coordinate(crime.Location?.Latitude)),
                CsvCell.Number(CrimeLocationDto.Coordinate(crime.Location?.Longitude)),
                CsvCell.Integer(crime.Location?.Street?.Id), CsvCell.Text(crime.Location?.Street?.Name),
                CsvCell.Text(crime.LocationType), CsvCell.Text(crime.Context),
                CsvCell.Text(crime.Outcome?.Category), CsvCell.Text(crime.Outcome?.Date)
            ]);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var file = ExportFile.Crimes(query);
        var count = await exporter.ExportAsync(file, rows, lease, cancellationToken);
        return new(true, "crimes", count, file.Filename, clock.GetUtcNow().ToUniversalTime());
    }
}
