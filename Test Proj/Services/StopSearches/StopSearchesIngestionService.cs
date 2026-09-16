using Test_Proj.Clients.PoliceApi;
using Test_Proj.Contracts.Requests;
using Test_Proj.Contracts.Responses;
using Test_Proj.Errors;
using Test_Proj.Export;
using Test_Proj.Validation;

namespace Test_Proj.Services.StopSearches;

public interface IStopSearchesIngestionService
{
    Task<IngestionResult> IngestAsync(LocationMonthRequest? request, CancellationToken cancellationToken);
}

public sealed class StopSearchesIngestionService(IPoliceApiClient client, ICsvExporter exporter,
    OperationLease admission, TimeProvider clock) : IStopSearchesIngestionService
{
    public async Task<IngestionResult> IngestAsync(LocationMonthRequest? request, CancellationToken cancellationToken)
    {
        var query = request?.Validate() ?? LocationMonth.Create(null, null, null);
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = admission.Acquire();
        var stops = await client.GetStopSearchesAsync(query, cancellationToken);
        if (stops is null) throw new PoliceApiException(PoliceApiFailure.InvalidPayload);
        var rows = new List<IReadOnlyList<CsvCell>>(stops.Count);
        foreach (var stop in stops)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stop is null || !stop.IsValid())
                throw new PoliceApiException(PoliceApiFailure.InvalidPayload);
            stop.TryTimestamp(out var timestamp);
            rows.Add([
                CsvCell.Text(stop.Type), CsvCell.Timestamp(timestamp), CsvCell.Text(stop.AgeRange),
                CsvCell.Text(stop.Gender), CsvCell.Text(stop.SelfDefinedEthnicity), CsvCell.Text(stop.OfficerDefinedEthnicity),
                CsvCell.Text(stop.Legislation), CsvCell.Text(stop.ObjectOfSearch), CsvCell.Text(stop.Outcome),
                CsvCell.Boolean(stop.InvolvedPerson), CsvCell.Boolean(stop.Operation), CsvCell.Text(stop.OperationName),
                CsvCell.Number(CrimeLocationDto.Coordinate(stop.Location?.Latitude)),
                CsvCell.Number(CrimeLocationDto.Coordinate(stop.Location?.Longitude)),
                CsvCell.Integer(stop.Location?.Street?.Id), CsvCell.Text(stop.Location?.Street?.Name)
            ]);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var file = ExportFile.StopSearches(query);
        var count = await exporter.ExportAsync(file, rows, lease, cancellationToken);
        return new(true, "stop-searches", count, file.Filename, clock.GetUtcNow().ToUniversalTime());
    }
}
