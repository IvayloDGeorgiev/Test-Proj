using System.Text;
using Microsoft.Extensions.Options;
using Test_Proj.Options;

namespace Test_Proj.Export;

public interface ICsvExporter
{
    Task<int> ExportAsync(ExportFile file, IEnumerable<IReadOnlyList<CsvCell>> rows,
        OperationLease.Lease lease, CancellationToken cancellationToken);
}

public sealed class CsvExporter : ICsvExporter
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly string root;
    private readonly OperationLease admission;
    private readonly IAtomicFileOperations files;
    private readonly ILogger<CsvExporter> logger;
    public CsvExporter(IOptions<ExportOptions> options, OperationLease admission,
        IAtomicFileOperations files, ILogger<CsvExporter> logger)
    {
        if (!new ExportOptionsValidator().Validate(null, options.Value).Succeeded)
            throw new ExportException("invalid_export_configuration");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Value.OutputRoot!));
        this.admission = admission;
        this.files = files;
        this.logger = logger;
    }

    public async Task<int> ExportAsync(ExportFile file, IEnumerable<IReadOnlyList<CsvCell>> rows,
        OperationLease.Lease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(lease);
        using var writeScope = lease.BeginWrite(admission);
        string? temporary = null;
        var created = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = ExportPath.Resolve(root, file.Filename);
            Directory.CreateDirectory(root);
            destination = ExportPath.Resolve(root, file.Filename);
            temporary = ExportPath.Resolve(root, $".export-{Guid.NewGuid():N}.tmp");
            var count = 0;
            await using (var stream = files.CreateTemporary(temporary))
            {
                created = true;
                await stream.WriteAsync(Utf8.GetBytes(string.Join(',', file.Headers) + "\r\n"), cancellationToken);
                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (row is null || row.Count != file.Headers.Count) throw new ExportException("invalid_csv_row");
                    var record = string.Join(',', row.Select(cell => cell.Encode())) + "\r\n";
                    await stream.WriteAsync(Utf8.GetBytes(record), cancellationToken);
                    count = checked(count + 1);
                }
                await stream.FlushAsync(cancellationToken);
                if (stream is FileStream fileStream) fileStream.Flush(flushToDisk: true);
            }
            // The trusted, account-owned root is checked again immediately before the atomic rename.
            ExportPath.Resolve(root, Path.GetFileName(temporary));
            ExportPath.Resolve(root, file.Filename);
            cancellationToken.ThrowIfCancellationRequested();
            files.Publish(temporary, destination);
            created = false;
            // Publication is the commit point: cancellation after it cannot undo a completed export.
            return count;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ExportException) { throw; }
        catch (Exception) { throw new ExportException("export_failed"); }
        finally
        {
            if (created && temporary != null)
            {
                try
                {
                    ExportPath.Resolve(root, Path.GetFileName(temporary));
                    files.DeleteTemporary(temporary);
                }
                catch (Exception)
                {
                    // Never follow an altered root or disclose a path, payload or exception text.
                    logger.LogWarning("Export temporary cleanup failed. Code: {Code}", "export_cleanup_failed");
                }
            }
        }
    }
}
