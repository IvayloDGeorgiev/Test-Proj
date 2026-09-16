using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Test_Proj.Clients.PoliceApi;
using Test_Proj.Contracts.Responses;
using Test_Proj.Controllers;
using Test_Proj.Errors;
using Test_Proj.Export;
using Test_Proj.Services.Forces;
using PoliceDataIngestion.Api.Tests.Export;

namespace PoliceDataIngestion.Api.Tests.Forces;

public sealed class ForcesTests
{
    [Fact]
    public async Task Ingest_OfficialFixture_PublishesExactSchemaAndSafeResult()
    {
        // Arrange: frozen official example, with a separate upstream DTO contract.
        const string json = """[{"id":"avon-and-somerset","name":"Avon and Somerset Constabulary"},{"id":"bedfordshire","name":"Bedfordshire Police"},{"id":"cambridgeshire","name":"Cambridgeshire Constabulary"}]""";
        using var files = new ExportTests.Fixture();
        var client = new FakeClient { Records = JsonSerializer.Deserialize<ForceDto[]>(json)! };
        var clock = new Clock();
        var service = new ForcesIngestionService(client, files.Writer, files.Gate, clock);
        // Act
        var result = await service.IngestAsync(default);
        // Assert
        Assert.Equal("id,name\r\navon-and-somerset,Avon and Somerset Constabulary\r\nbedfordshire,Bedfordshire Police\r\ncambridgeshire,Cambridgeshire Constabulary\r\n",
            await File.ReadAllTextAsync(files.Final));
        Assert.Equal(new IngestionResult(true, "forces", 3, "Forces.csv", clock.Now), result);
        Assert.Equal(TimeSpan.Zero, result.CompletedAtUtc.Offset);
        var response = JsonSerializer.Serialize(result, JsonSerializerOptions.Web);
        Assert.DoesNotContain(files.Root, response);
        using var document = JsonDocument.Parse(response);
        Assert.Equal(["success", "dataset", "recordCount", "filename", "completedAtUtc"],
            document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal([files.Final], Directory.GetFiles(files.Root));
        using var available = files.Gate.Acquire();
    }

    [Fact]
    public async Task Ingest_RepeatedThenEmpty_ReplacesWithHeaderOnlyUtf8File()
    {
        // Arrange
        using var files = new ExportTests.Fixture();
        var client = new FakeClient();
        var service = new ForcesIngestionService(client, files.Writer, files.Gate, new Clock());
        await service.IngestAsync(default);
        client.Records = [new() { Id = "id,one", Name = "  =\"警察\"\r\nnext" }];
        // Act
        var replaced = await service.IngestAsync(default);
        // Assert
        Assert.Equal(1, replaced.RecordCount);
        Assert.Equal("id,name\r\n\"id,one\",\"'  =\"\"警察\"\"\r\nnext\"\r\n", await File.ReadAllTextAsync(files.Final));
        client.Records = [];
        var empty = await service.IngestAsync(default);
        Assert.Equal(0, empty.RecordCount);
        Assert.True(empty.Success);
        Assert.Equal(Encoding.UTF8.GetBytes("id,name\r\n"), await File.ReadAllBytesAsync(files.Final));
        Assert.Equal([files.Final], Directory.GetFiles(files.Root));
    }

    [Theory]
    [InlineData(null, "name")]
    [InlineData("", "name")]
    [InlineData(" \t", "name")]
    [InlineData("id", null)]
    [InlineData("id", "")]
    [InlineData("id", "\r\n")]
    public async Task Ingest_InvalidRequiredField_RejectsAllRowsBeforeExport(string? id, string? name)
    {
        // Arrange
        var gate = new OperationLease();
        var exporter = new FakeExporter();
        var client = new FakeClient { Records = [new() { Id = "valid", Name = "valid" }, new() { Id = id, Name = name }] };
        var service = new ForcesIngestionService(client, exporter, gate, new Clock());
        // Act
        var error = await Assert.ThrowsAsync<PoliceApiException>(() => service.IngestAsync(default));
        // Assert
        Assert.Equal(PoliceApiFailure.InvalidPayload, error.Failure);
        Assert.Equal(0, exporter.Calls);
        using var available = gate.Acquire();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ingest_NullBoundaryData_RejectsBeforeExport(bool nullCollection)
    {
        // Arrange
        var gate = new OperationLease();
        var exporter = new FakeExporter();
        var client = new FakeClient { Records = nullCollection ? null! : [null!] };
        var service = new ForcesIngestionService(client, exporter, gate, new Clock());
        // Act
        var error = await Assert.ThrowsAsync<PoliceApiException>(() => service.IngestAsync(default));
        // Assert
        Assert.Equal(502, error.StatusCode);
        Assert.Equal(0, exporter.Calls);
        using var available = gate.Acquire();
    }

    [Fact]
    public async Task Ingest_AwaitingRetrievalAndPublication_HoldsLeaseAndReturnsOnlyAfterExport()
    {
        // Arrange
        var gate = new OperationLease();
        var retrieval = new TaskCompletionSource<IReadOnlyList<ForceDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publication = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exporting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var clock = new Clock();
        var client = new FakeClient { OnGet = _ => retrieval.Task };
        var exporter = new FakeExporter { OnExport = (_, _, lease, token) =>
        {
            Assert.NotNull(lease);
            Assert.Equal(cancellation.Token, token);
            exporting.SetResult();
            return publication.Task;
        } };
        var service = new ForcesIngestionService(client, exporter, gate, clock);
        // Act / Assert: a second request fails before calling either boundary.
        var first = service.IngestAsync(cancellation.Token);
        Assert.False(first.IsCompleted);
        Assert.Equal(cancellation.Token, client.Token);
        Assert.Equal(409, (await Assert.ThrowsAsync<ExportException>(() => service.IngestAsync(default))).StatusCode);
        Assert.Equal(1, client.Calls);
        Assert.Equal(0, exporter.Calls);
        retrieval.SetResult([new() { Id = "one", Name = "One" }]);
        await exporting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(first.IsCompleted);
        Assert.Throws<ExportException>(() => gate.Acquire());
        clock.Now = clock.Now.AddMinutes(1);
        publication.SetResult(1);
        var result = await first;
        Assert.Equal(clock.Now, result.CompletedAtUtc);
        Assert.Equal(1, result.RecordCount);
        Assert.True(result.Success);
        using var available = gate.Acquire();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ingest_BoundaryFailure_PropagatesAndReleasesLease(bool exportFailure)
    {
        // Arrange
        var gate = new OperationLease();
        Exception failure = exportFailure ? new ExportException("export_failed") : new PoliceApiException(PoliceApiFailure.UpstreamFailure);
        var client = new FakeClient { OnGet = exportFailure ? null : _ => Task.FromException<IReadOnlyList<ForceDto>>(failure) };
        var exporter = new FakeExporter { OnExport = (_, _, _, _) => Task.FromException<int>(failure) };
        var service = new ForcesIngestionService(client, exporter, gate, new Clock());
        // Act
        var observed = await Record.ExceptionAsync(() => service.IngestAsync(default));
        // Assert
        Assert.Same(failure, observed);
        Assert.Equal(exportFailure ? 1 : 0, exporter.Calls);
        using var available = gate.Acquire();
    }

    [Theory]
    [InlineData("before")]
    [InlineData("retrieval")]
    [InlineData("after-retrieval")]
    [InlineData("export")]
    public async Task Ingest_CallerCancellation_PropagatesAndReleasesLease(string phase)
    {
        // Arrange
        var gate = new OperationLease();
        using var cancellation = new CancellationTokenSource();
        var client = new FakeClient();
        var exporter = new FakeExporter();
        if (phase == "before") cancellation.Cancel();
        if (phase is "retrieval" or "after-retrieval") client.OnGet = token =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
            if (phase == "retrieval") token.ThrowIfCancellationRequested();
            return Task.FromResult(client.Records);
        };
        if (phase == "export") exporter.OnExport = (_, _, _, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
            return Task.FromCanceled<int>(token);
        };
        var service = new ForcesIngestionService(client, exporter, gate, new Clock());
        // Act
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.IngestAsync(cancellation.Token));
        // Assert
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(phase == "before" ? 0 : 1, client.Calls);
        Assert.Equal(phase == "export" ? 1 : 0, exporter.Calls);
        using var available = gate.Acquire();
    }

    [Fact]
    public async Task Ingest_CancellationAfterPublication_RetainsCommittedSuccess()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var operations = new ExportTests.FaultOperations { BeforePublish = cancellation.Cancel };
        using var files = new ExportTests.Fixture(operations);
        var service = new ForcesIngestionService(new FakeClient(), files.Writer, files.Gate, new Clock());
        // Act: cancellation arrives inside publication, after the exporter's final check.
        var result = await service.IngestAsync(cancellation.Token);
        // Assert
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(result.Success);
        Assert.Equal(1, result.RecordCount);
        Assert.Equal("id,name\r\none,One\r\n", await File.ReadAllTextAsync(files.Final));
        using var available = files.Gate.Acquire();
    }

    [Fact]
    public async Task Ingest_PublicationFailure_PreservesPreviousAndReleasesLease()
    {
        // Arrange
        using var files = new ExportTests.Fixture(new ExportTests.FaultOperations { Failure = "publish" });
        await File.WriteAllTextAsync(files.Final, "old complete");
        var service = new ForcesIngestionService(new FakeClient(), files.Writer, files.Gate, new Clock());
        // Act
        var error = await Assert.ThrowsAsync<ExportException>(() => service.IngestAsync(default));
        // Assert
        Assert.Equal("export_failed", error.Code);
        Assert.Equal("old complete", await File.ReadAllTextAsync(files.Final));
        Assert.Equal([files.Final], Directory.GetFiles(files.Root));
        using var available = files.Gate.Acquire();
    }

    [Fact]
    public async Task Forces_Controller_PassesRequestAbortedAndReturnsServiceMetadata()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var expected = new IngestionResult(true, "forces", 17, "Forces.csv", new Clock().Now);
        var service = new FakeService { Result = expected };
        var controller = new IngestionController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestAborted = cancellation.Token } }
        };
        // Act
        var response = await controller.Forces();
        // Assert
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal(200, ok.StatusCode);
        Assert.Same(expected, ok.Value);
        Assert.Equal(cancellation.Token, service.Token);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task Forces_ControllerFailure_DoesNotManufactureSuccess()
    {
        // Arrange
        var failure = new PoliceApiException(PoliceApiFailure.Timeout);
        var controller = new IngestionController(new FakeService { Failure = failure })
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        // Act
        var observed = await Assert.ThrowsAsync<PoliceApiException>(() => controller.Forces());
        // Assert
        Assert.Same(failure, observed);
    }

    internal sealed class FakeClient : IPoliceApiClient
    {
        public Task<IReadOnlyList<CrimeDto>> GetCrimesAsync(Test_Proj.Validation.LocationMonth request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IReadOnlyList<ForceDto> Records { get; set; } = [new() { Id = "one", Name = "One" }];
        public Func<CancellationToken, Task<IReadOnlyList<ForceDto>>>? OnGet { get; set; }
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<IReadOnlyList<StopSearchDto>> GetStopSearchesAsync(Test_Proj.Validation.LocationMonth request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ForceDto>> GetForcesAsync(CancellationToken cancellationToken = default)
        { Calls++; Token = cancellationToken; return OnGet?.Invoke(cancellationToken) ?? Task.FromResult(Records); }
    }
    internal sealed class FakeExporter : ICsvExporter
    {
        public int Calls { get; private set; }
        public Func<ExportFile, IEnumerable<IReadOnlyList<CsvCell>>, OperationLease.Lease, CancellationToken, Task<int>>? OnExport { get; set; }
        public Task<int> ExportAsync(ExportFile file, IEnumerable<IReadOnlyList<CsvCell>> rows, OperationLease.Lease lease, CancellationToken cancellationToken)
        { Calls++; return OnExport?.Invoke(file, rows, lease, cancellationToken) ?? Task.FromResult(rows.Count()); }
    }
    private sealed class FakeService : IForcesIngestionService
    {
        public IngestionResult Result { get; set; } = null!;
        public Exception? Failure { get; set; }
        public CancellationToken Token { get; private set; }
        public int Calls { get; private set; }
        public Task<IngestionResult> IngestAsync(CancellationToken cancellationToken)
        { Calls++; Token = cancellationToken; return Failure is null ? Task.FromResult(Result) : Task.FromException<IngestionResult>(Failure); }
    }
    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
