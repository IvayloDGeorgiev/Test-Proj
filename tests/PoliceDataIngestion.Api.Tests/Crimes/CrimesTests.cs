using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using PoliceDataIngestion.Api.Tests.Export;
using PoliceDataIngestion.Api.Tests.Forces;
using PoliceDataIngestion.Api.Tests.PoliceApi;
using Test_Proj.Clients.PoliceApi;
using Test_Proj.Contracts.Requests;
using Test_Proj.Contracts.Responses;
using Test_Proj.Controllers;
using Test_Proj.Errors;
using Test_Proj.Export;
using Test_Proj.Services.Crimes;
using Test_Proj.Validation;

namespace PoliceDataIngestion.Api.Tests.Crimes;

public sealed class CrimesTests
{
    private static readonly LocationMonthRequest Request = new(52.629729, -1.131592, "2024-01");
    private const string Header = "id,persistent_id,category,month,latitude,longitude,street_id,street_name,location_type,context,outcome_category,outcome_date\r\n";
    // Frozen from https://data.police.uk/docs/method/crime-street/ on 2026-09-16.
    private const string Official = """
        [{"category":"anti-social-behaviour","persistent_id":"","location_subtype":"","id":116208998,"location":{"latitude":"52.632805","street":{"id":1738842,"name":"On or near Campbell Street"},"longitude":"-1.124819"},"context":"","month":"2024-01","location_type":"Force","outcome_status":null},
        {"category":"bicycle-theft","persistent_id":"fef00968b9b066ad38660e64f6e746eb1dfc27b22af4a023924daebfcea2ad39","location_subtype":"","id":116201252,"location":{"latitude":"52.629043","street":{"id":1737492,"name":"On or near Roman Street"},"longitude":"-1.148962"},"context":"","month":"2024-01","location_type":"Force","outcome_status":{"category":"Investigation complete; no suspect identified","date":"2024-01"}}]
        """;

    [Fact]
    public async Task Crimes_OfficialFixture_UsesInvariantQueryAndPublishesExactCsv()
    {
        // Arrange
        using var files = new ExportTests.Fixture();
        using var transport = new TransportFixture((_, _, _) => Task.FromResult(TransportFixture.Response(body: Official)));
        var clock = new ForcesTests.Clock();
        var service = new CrimesIngestionService(transport.Client, files.Writer, files.Gate, clock);
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            // Act
            var result = await service.IngestAsync(Request, default);
            // Assert
            Assert.Equal("https://data.police.uk/api/crimes-street/all-crime?lat=52.629729&lng=-1.131592&date=2024-01", Assert.Single(transport.Handler.Uris).AbsoluteUri);
            Assert.Equal(new IngestionResult(true, "crimes", 2, "Crimes_2024-01.csv", clock.Now), result);
            var expected = Header + "116208998,,anti-social-behaviour,2024-01,52.632805,-1.124819,1738842,On or near Campbell Street,Force,,,\r\n" +
                "116201252,fef00968b9b066ad38660e64f6e746eb1dfc27b22af4a023924daebfcea2ad39,bicycle-theft,2024-01,52.629043,-1.148962,1737492,On or near Roman Street,Force,,Investigation complete; no suspect identified,2024-01\r\n";
            Assert.Equal(Encoding.UTF8.GetBytes(expected), await File.ReadAllBytesAsync(Path.Combine(files.Root, result.Filename)));
            Assert.DoesNotContain(files.Root, JsonSerializer.Serialize(result));
            Assert.Single(Directory.GetFiles(files.Root));
            using var free = files.Gate.Acquire();
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    public static TheoryData<double?, double?, string?> InvalidRequests => new()
    {
        {null, 0, "2024-01"}, {0, null, "2024-01"}, {double.NaN, 0, "2024-01"},
        {double.PositiveInfinity, 0, "2024-01"}, {0, double.NegativeInfinity, "2024-01"},
        {-90.01, 0, "2024-01"}, {90.01, 0, "2024-01"}, {0, -180.01, "2024-01"}, {0, 180.01, "2024-01"},
        {0, 0, null}, {0, 0, ""}, {0, 0, "0000-01"}, {0, 0, "2024-00"}, {0, 0, "2024-13"},
        {0, 0, "2024-1"}, {0, 0, "２０２４-01"}, {0, 0, "2024-01/../x"}, {0, 0, "2024-01 "}
    };

    [Theory, MemberData(nameof(InvalidRequests))]
    public async Task Crimes_InvalidRequest_PerformsNoHttpOrFileWork(double? latitude, double? longitude, string? month)
    {
        // Arrange: occupied admission proves validation also precedes acquiring the lease.
        using var transport = new TransportFixture((_, _, _) => throw new InvalidOperationException("HTTP must not run"));
        var exporter = new ForcesTests.FakeExporter();
        var gate = new OperationLease();
        using var occupied = gate.Acquire();
        var service = new CrimesIngestionService(transport.Client, exporter, gate, TimeProvider.System);
        // Act
        var error = await Assert.ThrowsAsync<RequestValidationException>(() => service.IngestAsync(new(latitude, longitude, month), default));
        // Assert
        Assert.NotEmpty(error.Errors);
        Assert.Equal(0, transport.Handler.Calls);
        Assert.Equal(0, exporter.Calls);
    }

    [Theory]
    [InlineData(-90, -180, "0001-01")]
    [InlineData(90, 180, "9999-12")]
    [InlineData(0, 0, "2024-02")]
    public async Task Crimes_InclusiveBoundaries_ArePassedToClient(double lat, double lng, string month)
    {
        // Arrange
        var client = new Client { Records = [] };
        var exporter = new ForcesTests.FakeExporter();
        var service = new CrimesIngestionService(client, exporter, new(), TimeProvider.System);
        // Act
        var result = await service.IngestAsync(new(lat, lng, month), default);
        // Assert
        Assert.Equal(LocationMonth.Create(lat, lng, month), client.Query);
        Assert.Equal($"Crimes_{month}.csv", result.Filename);
        Assert.Equal(0, result.RecordCount);
        Assert.Equal(1, exporter.Calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{")]
    [InlineData("[{\"category\":\"x\",\"month\":\"2024-01\"}]")]
    [InlineData("[{\"id\":0,\"category\":\"x\",\"month\":\"2024-01\"}]")]
    [InlineData("[{\"id\":1,\"category\":\" \",\"month\":\"2024-01\"}]")]
    [InlineData("[{\"id\":1,\"category\":\"x\"}]")]
    [InlineData("[{\"id\":1,\"category\":\"x\",\"month\":\"2024-02\"}]")]
    [InlineData("[{\"id\":\"1\",\"category\":\"x\",\"month\":\"2024-01\"}]")]
    [InlineData("[{\"id\":1.5,\"category\":\"x\",\"month\":\"2024-01\"}]")]
    [InlineData("[{\"id\":1,\"category\":\"x\",\"month\":\"2024-01\",\"location\":[]}]")]
    [InlineData("[{\"id\":1,\"category\":\"x\",\"month\":\"2024-01\",\"outcome_status\":false}]")]
    [InlineData("[{\"id\":1,\"category\":\"x\",\"month\":\"2024-01\",\"location\":{\"latitude\":\"NaN\"}}]")]
    [InlineData("[{\"id\":1,\"category\":\"x\",\"month\":\"2024-01\",\"location\":{\"longitude\":\"181\"}}]")]
    [InlineData("[{\"id\":1,\"category\":\"x\",\"month\":\"2024-01\",\"location\":{\"latitude\":\"52,3\"}}]")]
    public async Task Crimes_InvalidPayload_FailsWithoutRetryOrExport(string json)
    {
        // Arrange
        using var transport = new TransportFixture((_, _, _) => Task.FromResult(TransportFixture.Response(body: json)));
        var exporter = new ForcesTests.FakeExporter();
        var gate = new OperationLease();
        var service = new CrimesIngestionService(transport.Client, exporter, gate, TimeProvider.System);
        // Act
        var error = await Assert.ThrowsAsync<PoliceApiException>(() => service.IngestAsync(Request, default));
        // Assert
        Assert.Equal(PoliceApiFailure.InvalidPayload, error.Failure);
        Assert.Equal(1, transport.Handler.Calls);
        Assert.Equal(0, exporter.Calls);
        using var free = gate.Acquire();
    }

    [Fact]
    public async Task Crimes_OptionalNestedAndUnsafeText_MapsEmptyCellsAndProtectsTextThenEmptyReplaces()
    {
        // Arrange
        using var files = new ExportTests.Fixture();
        var client = new Client { Records = [Valid(), Valid() with { Id = 2, Location = new() { Longitude = "-1.5", Street = new() { Name = "  =\"警察\"\r\nnext" } }, Outcome = new() { Category = "a,b" } }] };
        var service = new CrimesIngestionService(client, files.Writer, files.Gate, TimeProvider.System);
        var path = Path.Combine(files.Root, "Crimes_2024-01.csv");
        // Act
        var result = await service.IngestAsync(Request, default);
        // Assert
        Assert.Equal(2, result.RecordCount);
        Assert.Equal(Header + "1,,theft,2024-01,,,,,,,,\r\n2,,theft,2024-01,,-1.5,,\"'  =\"\"警察\"\"\r\nnext\",,,\"a,b\",\r\n", await File.ReadAllTextAsync(path));
        client.Records = [];
        var empty = await service.IngestAsync(Request with { Latitude = 10 }, default);
        Assert.Equal(0, empty.RecordCount);
        Assert.Equal(Encoding.UTF8.GetBytes(Header), await File.ReadAllBytesAsync(path));
        Assert.Equal([path], Directory.GetFiles(files.Root));
    }

    [Theory]
    [InlineData("null-list")]
    [InlineData("null-row")]
    [InlineData("month")]
    [InlineData("id")]
    [InlineData("category")]
    [InlineData("coordinate")]
    public async Task Crimes_InvalidClientBoundary_RejectsBeforeExport(string kind)
    {
        // Arrange
        var invalid = kind switch { "month" => Valid() with { Month = "2024-02" }, "id" => Valid() with { Id = null }, "category" => Valid() with { Category = null }, "coordinate" => Valid() with { Location = new() { Latitude = "Infinity" } }, _ => null! };
        var client = new Client { Records = kind == "null-list" ? null! : [Valid(), invalid] };
        var exporter = new ForcesTests.FakeExporter();
        var gate = new OperationLease();
        var service = new CrimesIngestionService(client, exporter, gate, TimeProvider.System);
        // Act
        var error = await Assert.ThrowsAsync<PoliceApiException>(() => service.IngestAsync(Request, default));
        // Assert
        Assert.Equal(502, error.StatusCode);
        Assert.Equal(0, exporter.Calls);
        using var free = gate.Acquire();
    }

    [Fact]
    public async Task Crimes_PendingRetrievalAndExport_HoldsLeaseAndUsesPublishedCount()
    {
        // Arrange
        var retrieval = new TaskCompletionSource<IReadOnlyList<CrimeDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publication = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var client = new Client { OnGet = _ => retrieval.Task };
        var gate = new OperationLease();
        var exporter = new ForcesTests.FakeExporter { OnExport = (_, _, _, token) => { Assert.Equal(cancellation.Token, token); entered.SetResult(); return publication.Task; } };
        var clock = new ForcesTests.Clock();
        var service = new CrimesIngestionService(client, exporter, gate, clock);
        // Act / Assert
        var task = service.IngestAsync(Request, cancellation.Token);
        Assert.False(task.IsCompleted);
        Assert.Equal(cancellation.Token, client.Token);
        Assert.Equal(409, (await Assert.ThrowsAsync<ExportException>(() => service.IngestAsync(Request, default))).StatusCode);
        Assert.Equal(1, client.Calls);
        retrieval.SetResult([Valid()]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(task.IsCompleted);
        Assert.Throws<ExportException>(() => gate.Acquire());
        clock.Now = clock.Now.AddMinutes(1);
        publication.SetResult(7);
        var result = await task;
        Assert.Equal(7, result.RecordCount);
        Assert.Equal(clock.Now, result.CompletedAtUtc);
        using var free = gate.Acquire();
    }

    [Theory]
    [InlineData("before")]
    [InlineData("retrieval")]
    [InlineData("after-retrieval")]
    [InlineData("export")]
    public async Task Crimes_Cancellation_PropagatesAndReleases(string phase)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var client = new Client();
        var exporter = new ForcesTests.FakeExporter();
        var gate = new OperationLease();
        if (phase == "before") cancellation.Cancel();
        if (phase is "retrieval" or "after-retrieval") client.OnGet = token => { cancellation.Cancel(); if (phase == "retrieval") token.ThrowIfCancellationRequested(); return Task.FromResult(client.Records); };
        if (phase == "export") exporter.OnExport = (_, _, _, token) => { cancellation.Cancel(); return Task.FromCanceled<int>(token); };
        var service = new CrimesIngestionService(client, exporter, gate, TimeProvider.System);
        // Act
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.IngestAsync(Request, cancellation.Token));
        // Assert
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(phase == "before" ? 0 : 1, client.Calls);
        Assert.Equal(phase == "export" ? 1 : 0, exporter.Calls);
        using var free = gate.Acquire();
    }

    [Fact]
    public async Task Crimes_PublicationFailure_PreservesOldFileAndCleansTemporary()
    {
        // Arrange
        using var files = new ExportTests.Fixture(new ExportTests.FaultOperations { Failure = "publish" });
        var path = Path.Combine(files.Root, "Crimes_2024-01.csv");
        await File.WriteAllTextAsync(path, "old complete");
        var service = new CrimesIngestionService(new Client(), files.Writer, files.Gate, TimeProvider.System);
        // Act
        var error = await Assert.ThrowsAsync<ExportException>(() => service.IngestAsync(Request, default));
        // Assert
        Assert.Equal("export_failed", error.Code);
        Assert.Equal("old complete", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(files.Root));
        using var free = files.Gate.Acquire();
    }

    [Fact]
    public async Task Crimes_CancellationAfterPublication_ReturnsCommittedSuccess()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        using var files = new ExportTests.Fixture(new ExportTests.FaultOperations { BeforePublish = cancellation.Cancel });
        var service = new CrimesIngestionService(new Client(), files.Writer, files.Gate, TimeProvider.System);
        // Act
        var result = await service.IngestAsync(Request, cancellation.Token);
        // Assert
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(result.Success);
        Assert.Equal(Header + "1,,theft,2024-01,,,,,,,,\r\n", await File.ReadAllTextAsync(Path.Combine(files.Root, result.Filename)));
    }

    [Fact]
    public async Task Crimes_Controller_PropagatesTokenAndResult()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var expected = new IngestionResult(true, "crimes", 3, "Crimes_2024-01.csv", DateTimeOffset.UtcNow);
        var service = new Service(expected);
        var controller = new CrimesIngestionController(service) { ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestAborted = cancellation.Token } } };
        // Act
        var response = await controller.Crimes(Request);
        // Assert
        Assert.Same(expected, Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Same(Request, service.Request);
        Assert.Equal(cancellation.Token, service.Token);
    }

    [Fact]
    public async Task Crimes_NullRequest_ReturnsSafeValidationProblemWithoutSideEffects()
    {
        // Arrange
        var client = new Client();
        var exporter = new ForcesTests.FakeExporter();
        var service = new CrimesIngestionService(client, exporter, new(), TimeProvider.System);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new IngestionErrorMiddleware(async _ => await service.IngestAsync(null, default), NullLogger<IngestionErrorMiddleware>.Instance);
        // Act
        await middleware.InvokeAsync(context);
        // Assert
        Assert.Equal(400, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("validation_failed", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("errors").EnumerateObject().Count());
        Assert.Equal(0, client.Calls);
        Assert.Equal(0, exporter.Calls);
    }

    [Theory]
    [InlineData(400, 502)]
    [InlineData(404, 502)]
    [InlineData(429, 503)]
    [InlineData(503, 502)]
    public async Task Crimes_UpstreamFailure_NoPublicationAndLeaseReleased(int status, int expected)
    {
        // Arrange
        using var transport = new TransportFixture((_, _, _) => Task.FromResult(TransportFixture.Response(status)), new() { MaximumAttempts = 1 });
        var exporter = new ForcesTests.FakeExporter();
        var gate = new OperationLease();
        var service = new CrimesIngestionService(transport.Client, exporter, gate, TimeProvider.System);
        // Act
        var error = await Assert.ThrowsAsync<PoliceApiException>(() => service.IngestAsync(Request, default));
        // Assert
        Assert.Equal(expected, error.StatusCode);
        Assert.Equal(1, transport.Handler.Calls);
        Assert.Equal(0, exporter.Calls);
        using var free = gate.Acquire();
    }

    [Fact]
    public async Task Crimes_HttpBodyCancellation_StopsReadWithoutExport()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var stream = new BlockingStream();
        using var transport = new TransportFixture((_, _, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        var exporter = new ForcesTests.FakeExporter();
        var gate = new OperationLease();
        var service = new CrimesIngestionService(transport.Client, exporter, gate, TimeProvider.System);
        // Act
        var task = service.IngestAsync(Request, cancellation.Token);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        // Assert
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(stream.Disposed);
        Assert.Equal(1, transport.Handler.Calls);
        Assert.Equal(0, exporter.Calls);
        using var free = gate.Acquire();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Crimes_ResponseLimits_RejectBeforeExport(bool records)
    {
        // Arrange
        using var transport = new TransportFixture((_, _, _) => Task.FromResult(TransportFixture.Response(body: Official)),
            records ? new() { MaximumRecords = 1 } : new() { MaximumResponseBytes = 16 });
        var exporter = new ForcesTests.FakeExporter();
        var service = new CrimesIngestionService(transport.Client, exporter, new(), TimeProvider.System);
        // Act
        var error = await Assert.ThrowsAsync<PoliceApiException>(() => service.IngestAsync(Request, default));
        // Assert
        Assert.Equal(PoliceApiFailure.ResponseLimit, error.Failure);
        Assert.Equal(0, exporter.Calls);
        Assert.Equal(1, transport.Handler.Calls);
    }

    [Theory]
    [InlineData("[]", 0)]
    [InlineData("[{\"id\":1,\"category\":\"theft\",\"month\":\"2024-01\",\"location\":{},\"outcome_status\":{},\"future\":42}]", 1)]
    public async Task Crimes_EmptyOrOptionalObjects_PublishesExpectedRows(string json, int count)
    {
        // Arrange
        using var files = new ExportTests.Fixture();
        using var transport = new TransportFixture((_, _, _) => Task.FromResult(TransportFixture.Response(body: json)));
        var service = new CrimesIngestionService(transport.Client, files.Writer, files.Gate, TimeProvider.System);
        // Act
        var result = await service.IngestAsync(Request, default);
        // Assert
        Assert.Equal(count, result.RecordCount);
        Assert.Equal(Header + (count == 0 ? "" : "1,,theft,2024-01,,,,,,,,\r\n"), await File.ReadAllTextAsync(Path.Combine(files.Root, result.Filename)));
    }
    private static CrimeDto Valid() => new() { Id = 1, Category = "theft", Month = "2024-01" };
    private sealed class Client : IPoliceApiClient
    {
        public IReadOnlyList<CrimeDto> Records { get; set; } = [Valid()];
        public Func<CancellationToken, Task<IReadOnlyList<CrimeDto>>>? OnGet { get; set; }
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }
        public LocationMonth? Query { get; private set; }
        public Task<IReadOnlyList<CrimeDto>> GetCrimesAsync(LocationMonth request, CancellationToken cancellationToken = default)
        { Calls++; Query = request; Token = cancellationToken; return OnGet?.Invoke(cancellationToken) ?? Task.FromResult(Records); }
        public Task<IReadOnlyList<ForceDto>> GetForcesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Service(IngestionResult result) : ICrimesIngestionService
    {
        public LocationMonthRequest? Request { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<IngestionResult> IngestAsync(LocationMonthRequest? request, CancellationToken cancellationToken)
        { Request = request; Token = cancellationToken; return Task.FromResult(result); }
    }
}
