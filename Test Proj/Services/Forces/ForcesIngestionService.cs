using Test_Proj.Clients.PoliceApi;
using Test_Proj.Contracts.Responses;
using Test_Proj.Errors;
using Test_Proj.Export;

namespace Test_Proj.Services.Forces;

public interface IForcesIngestionService
{
    Task<IngestionResult> IngestAsync(CancellationToken cancellationToken);
}

public sealed class ForcesIngestionService(IPoliceApiClient client, ICsvExporter exporter,
    OperationLease admission, TimeProvider clock) : IForcesIngestionService
{
    public async Task<IngestionResult> IngestAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = admission.Acquire();
        var forces = await client.GetForcesAsync(cancellationToken);
        if (forces is null) throw new PoliceApiException(PoliceApiFailure.InvalidPayload);
        var rows = new List<IReadOnlyList<CsvCell>>(forces.Count);
        foreach (var force in forces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (force is null || !force.IsValid())
                throw new PoliceApiException(PoliceApiFailure.InvalidPayload);
            rows.Add([CsvCell.Text(force.Id), CsvCell.Text(force.Name)]);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var count = await exporter.ExportAsync(ExportFile.Forces, rows, lease, cancellationToken);
        // Publication is the commit point; later cancellation cannot roll it back.
        return new(true, "forces", count, ExportFile.Forces.Filename, clock.GetUtcNow().ToUniversalTime());
    }
}
