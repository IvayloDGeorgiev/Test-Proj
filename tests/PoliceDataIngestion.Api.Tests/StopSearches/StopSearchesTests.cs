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
using Test_Proj.Services.StopSearches;
using Test_Proj.Validation;

namespace PoliceDataIngestion.Api.Tests.StopSearches;

public sealed class StopSearchesTests
{
    private static readonly LocationMonthRequest Request = new(52.629729, -1.131592, "2024-01");
    private const string Header = "type,datetime,age_range,gender,self_defined_ethnicity,officer_defined_ethnicity,legislation,object_of_search,outcome,involved_person,operation,operation_name,latitude,longitude,street_id,street_name\r\n";
    // Frozen official stops-street example, checked 2026-09-16; unknown fields intentionally retained.
    private const string Official = """
    [{"age_range":"10-17","officer_defined_ethnicity":"White","involved_person":true,
    "self_defined_ethnicity":"White - English/Welsh/Scottish/Northern Irish/British","gender":"Male",
    "legislation":"Police and Criminal Evidence Act 1984 (section 1)","outcome_linked_to_object_of_search":null,
    "datetime":"2024-01-04T07:41:00+00:00","outcome_object":{"id":"bu-no-further-action","name":"A no further action disposal"},
    "location":{"latitude":"52.630693","street":{"id":1738552,"name":"On or near Wellington Street"},"longitude":"-1.129885"},
    "object_of_search":"Offensive weapons","operation":null,"outcome":"A no further action disposal","type":"Person search",
    "operation_name":null,"removal_of_more_than_outer_clothing":false},
    {"type":"Vehicle search","datetime":"2024-01-05T15:00:00+01:00","involved_person":false,"operation":true}]
    """;

    [Fact]
    public async Task StopSearches_OfficialFixture_PublishesExactSchemaAndInvariantQuery()
    {
        // Arrange
        using var files = new ExportTests.Fixture();
        using var transport = new TransportFixture((_, _, _) => Task.FromResult(TransportFixture.Response(body: Official)));
        var clock = new ForcesTests.Clock();
        var service = new StopSearchesIngestionService(transport.Client, files.Writer, files.Gate, clock);
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            // Act
            var result = await service.IngestAsync(Request, default);
            // Assert
            Assert.Equal("https://data.police.uk/api/stops-street?lat=52.629729&lng=-1.131592&date=2024-01", Assert.Single(transport.Handler.Uris).AbsoluteUri);
            Assert.Equal(new IngestionResult(true, "stop-searches", 2, "StopSearches_2024-01.csv", clock.Now), result);
            var expected = Header + "Person search,2024-01-04T07:41:00.0000000+00:00,10-17,Male,White - English/Welsh/Scottish/Northern Irish/British,White,Police and Criminal Evidence Act 1984 (section 1),Offensive weapons,A no further action disposal,true,,,52.630693,-1.129885,1738552,On or near Wellington Street\r\n" +
                "Vehicle search,2024-01-05T15:00:00.0000000+01:00,,,,,,,,false,true,,,,,\r\n";
            Assert.Equal(Encoding.UTF8.GetBytes(expected), await File.ReadAllBytesAsync(Path.Combine(files.Root, result.Filename)));
            Assert.DoesNotContain(files.Root, JsonSerializer.Serialize(result));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    public static TheoryData<double?, double?, string?> InvalidRequests => Crimes.CrimesTests.InvalidRequests;
    [Theory, MemberData(nameof(InvalidRequests))]
    public async Task StopSearches_InvalidRequest_PerformsNoHttpOrFileWork(double? lat, double? lng, string? month)
    {
        // Arrange
        using var transport = new TransportFixture((_, _, _) => throw new InvalidOperationException());
        var exporter = new ForcesTests.FakeExporter();
        var gate = new OperationLease();
        using var occupied = gate.Acquire();
        var service = new StopSearchesIngestionService(transport.Client, exporter, gate, TimeProvider.System);
        // Act
        var error = await Assert.ThrowsAsync<RequestValidationException>(() => service.IngestAsync(new(lat, lng, month), default));
        // Assert
        Assert.NotEmpty(error.Errors);
        Assert.Equal(0, transport.Handler.Calls);
        Assert.Equal(0, exporter.Calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{")]
    [InlineData("[{}]")]
    [InlineData("[{\"type\":\"x\"}]")]
    [InlineData("[{\"type\":false,\"datetime\":\"2024-01-01T00:00:00Z\"}]")]
    public async Task StopSearches_InvalidArray_FailsWithoutExport(string json) => await AssertInvalid(json);

    [Theory]
    [InlineData("type", "null")]
    [InlineData("type", "\" \"")]
    [InlineData("datetime", "null")]
    [InlineData("datetime", "\"2024-01-01\"")]
    [InlineData("datetime", "\"2024-01-01T00:00:00\"")]
    [InlineData("datetime", "\"2024-02-30T00:00:00Z\"")]
    [InlineData("datetime", "\"2024-01-01T00:00:00+15:00\"")]
    [InlineData("datetime", "\"2024-01-01T00:00:00Z \"")]
    [InlineData("datetime", "123")]
    [InlineData("operation", "\"false\"")]
    [InlineData("involved_person", "0")]
    [InlineData("age_range", "42")]
    [InlineData("location", "[]")]
    [InlineData("location", "{\"latitude\":\"NaN\"}")]
    [InlineData("location", "{\"longitude\":\"181\"}")]
    [InlineData("location", "{\"street\":{\"id\":-1}}")]
    [InlineData("outcome", "true")]
    [InlineData("outcome", "{}")]
    public async Task StopSearches_InvalidField_FailsBeforeExport(string field, string value)
    {
        var obj = System.Text.Json.Nodes.JsonNode.Parse("{\"type\":\"Person search\",\"datetime\":\"2024-01-01T00:00:00Z\"}")!;
        obj[field] = System.Text.Json.Nodes.JsonNode.Parse(value);
        await AssertInvalid("[" + obj.ToJsonString() + "]");
    }

    private static async Task AssertInvalid(string json)
    {
        // Arrange
        using var transport = new TransportFixture((_, _, _) => Task.FromResult(TransportFixture.Response(body: json)));
        var exporter = new ForcesTests.FakeExporter();
        var gate = new OperationLease();
        var service = new StopSearchesIngestionService(transport.Client, exporter, gate, TimeProvider.System);
        // Act
        var error = await Assert.ThrowsAsync<PoliceApiException>(() => service.IngestAsync(Request, default));
        // Assert
        Assert.Equal(PoliceApiFailure.InvalidPayload, error.Failure);
        Assert.Equal(1, transport.Handler.Calls);
        Assert.Equal(0, exporter.Calls);
        using var free = gate.Acquire();
    }

    [Theory]
    [InlineData("2024-01-01T00:01:02.1234567-05:30", "2024-01-01T00:01:02.1234567-05:30")]
    [InlineData("2024-01-01T00:00:00Z", "2024-01-01T00:00:00.0000000+00:00")]
    public async Task StopSearches_OptionalValuesAndOffsets_PreserveValuesAndEmptyReplacement(string timestamp, string expected)
    {
        // Arrange
        using var files = new ExportTests.Fixture();
        var client = new Client { Records = [Valid() with { Datetime = timestamp, Type = " =警察", Outcome = "a,\"b\"", InvolvedPerson = true, Operation = false }] };
        var service = new StopSearchesIngestionService(client, files.Writer, files.Gate, TimeProvider.System);
        // Act
        var result = await service.IngestAsync(Request, default);
        // Assert
        Assert.Equal(Header + "' =警察," + expected + ",,,,,,,\"a,\"\"b\"\"\",true,false,,,,,\r\n", await File.ReadAllTextAsync(Path.Combine(files.Root, result.Filename)));
        client.Records = [];
        var empty = await service.IngestAsync(Request, default);
        Assert.Equal(0, empty.RecordCount);
        Assert.Equal(Header, await File.ReadAllTextAsync(Path.Combine(files.Root, result.Filename)));
    }
    [Fact]
    public async Task StopSearches_PendingRetrievalAndExport_HoldsLeaseAndUsesPublishedCount()
    {
        // Arrange
        var retrieval = new TaskCompletionSource<IReadOnlyList<StopSearchDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publication = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var client = new Client { OnGet = _ => retrieval.Task };
        var gate = new OperationLease();
        var exporter = new ForcesTests.FakeExporter { OnExport = (_, _, _, token) => { Assert.Equal(cancellation.Token, token); entered.SetResult(); return publication.Task; } };
        var clock = new ForcesTests.Clock();
        var service = new StopSearchesIngestionService(client, exporter, gate, clock);
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
    public async Task StopSearches_Cancellation_PropagatesAndReleases(string phase)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var client = new Client();
        var exporter = new ForcesTests.FakeExporter();
        var gate = new OperationLease();
        if (phase == "before") cancellation.Cancel();
        if (phase is "retrieval" or "after-retrieval") client.OnGet = token => { cancellation.Cancel(); if (phase == "retrieval") token.ThrowIfCancellationRequested(); return Task.FromResult(client.Records); };
        if (phase == "export") exporter.OnExport = (_, _, _, token) => { cancellation.Cancel(); return Task.FromCanceled<int>(token); };
        var service = new StopSearchesIngestionService(client, exporter, gate, TimeProvider.System);
        // Act
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.IngestAsync(Request, cancellation.Token));
        // Assert
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(phase == "before" ? 0 : 1, client.Calls);
        Assert.Equal(phase == "export" ? 1 : 0, exporter.Calls);
        using var free = gate.Acquire();
    }

    [Fact]
    public async Task StopSearches_PublicationFailure_PreservesOldFileAndCleansTemporary()
    {
        // Arrange
        using var files = new ExportTests.Fixture(new ExportTests.FaultOperations { Failure = "publish" });
        var path = Path.Combine(files.Root, "StopSearches_2024-01.csv");
        await File.WriteAllTextAsync(path, "old complete");
        var service = new StopSearchesIngestionService(new Client(), files.Writer, files.Gate, TimeProvider.System);
        // Act
        var error = await Assert.ThrowsAsync<ExportException>(() => service.IngestAsync(Request, default));
        // Assert
        Assert.Equal("export_failed", error.Code);
        Assert.Equal("old complete", await File.ReadAllTextAsync(path));
        Assert.Equal([path], Directory.GetFiles(files.Root));
        using var free = files.Gate.Acquire();
    }

    [Fact]
    public async Task StopSearches_CancellationAfterPublication_ReturnsCommittedSuccess()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        using var files = new ExportTests.Fixture(new ExportTests.FaultOperations { BeforePublish = cancellation.Cancel });
        var service = new StopSearchesIngestionService(new Client(), files.Writer, files.Gate, TimeProvider.System);
        // Act
        var result = await service.IngestAsync(Request, cancellation.Token);
        // Assert
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(result.Success);
        Assert.Equal(Header + "Person search,2024-01-01T00:00:00.0000000+00:00,,,,,,,,,,,,,,\r\n", await File.ReadAllTextAsync(Path.Combine(files.Root, result.Filename)));
    }

    [Fact]
    public async Task StopSearches_Controller_PropagatesTokenAndResult()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var expected = new IngestionResult(true, "stop-searches", 3, "StopSearches_2024-01.csv", DateTimeOffset.UtcNow);
        var service = new Service(expected);
        var controller = new StopSearchesIngestionController(service) { ControllerContext = new() { HttpContext = new DefaultHttpContext { RequestAborted = cancellation.Token } } };
        // Act
        var response = await controller.StopSearches(Request);
        // Assert
        Assert.Same(expected, Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Same(Request, service.Request);
        Assert.Equal(cancellation.Token, service.Token);
    }

    [Fact]
    public async Task StopSearches_NullRequest_ReturnsSafeValidationProblemWithoutSideEffects()
    {
        // Arrange
        var client = new Client();
        var exporter = new ForcesTests.FakeExporter();
        var service = new StopSearchesIngestionService(client, exporter, new(), TimeProvider.System);
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
    public async Task StopSearches_UpstreamFailure_NoPublicationAndLeaseReleased(int status, int expected)
    {
        // Arrange
        using var transport = new TransportFixture((_, _, _) => Task.FromResult(TransportFixture.Response(status)), new() { MaximumAttempts = 1 });
        var exporter = new ForcesTests.FakeExporter();
        var gate = new OperationLease();
        var service = new StopSearchesIngestionService(transport.Client, exporter, gate, TimeProvider.System);
        // Act
        var error = await Assert.ThrowsAsync<PoliceApiException>(() => service.IngestAsync(Request, default));
        // Assert
        Assert.Equal(expected, error.StatusCode);
        Assert.Equal(1, transport.Handler.Calls);
        Assert.Equal(0, exporter.Calls);
        using var free = gate.Acquire();
    }

    [Fact]
    public async Task StopSearches_HttpBodyCancellation_StopsReadWithoutExport()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var stream = new BlockingStream();
        using var transport = new TransportFixture((_, _, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        var exporter = new ForcesTests.FakeExporter();
        var gate = new OperationLease();
        var service = new StopSearchesIngestionService(transport.Client, exporter, gate, TimeProvider.System);
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
    public async Task StopSearches_ResponseLimits_RejectBeforeExport(bool records)
    {
        // Arrange
        using var transport = new TransportFixture((_, _, _) => Task.FromResult(TransportFixture.Response(body: Official)),
            records ? new() { MaximumRecords = 1 } : new() { MaximumResponseBytes = 16 });
        var exporter = new ForcesTests.FakeExporter();
        var service = new StopSearchesIngestionService(transport.Client, exporter, new(), TimeProvider.System);
        // Act
        var error = await Assert.ThrowsAsync<PoliceApiException>(() => service.IngestAsync(Request, default));
        // Assert
        Assert.Equal(PoliceApiFailure.ResponseLimit, error.Failure);
        Assert.Equal(0, exporter.Calls);
        Assert.Equal(1, transport.Handler.Calls);
    }

    [Theory]
    [InlineData("[]", 0)]
    [InlineData("[{\"type\":\"Person search\",\"datetime\":\"2024-01-01T00:00:00Z\",\"location\":{},\"future\":42}]", 1)]
    public async Task StopSearches_EmptyOrOptionalObjects_PublishesExpectedRows(string json, int count)
    {
        // Arrange
        using var files = new ExportTests.Fixture();
        using var transport = new TransportFixture((_, _, _) => Task.FromResult(TransportFixture.Response(body: json)));
        var service = new StopSearchesIngestionService(transport.Client, files.Writer, files.Gate, TimeProvider.System);
        // Act
        var result = await service.IngestAsync(Request, default);
        // Assert
        Assert.Equal(count, result.RecordCount);
        Assert.Equal(Header + (count == 0 ? "" : "Person search,2024-01-01T00:00:00.0000000+00:00,,,,,,,,,,,,,,\r\n"), await File.ReadAllTextAsync(Path.Combine(files.Root, result.Filename)));
    }
    [Theory]
    [InlineData("false", "false")]
    [InlineData("null", "")]
    [InlineData("\"\"", "")]
    public async Task StopSearches_OutcomeContract_MapsFalseAndMissing(string json, string expected)
    {
        // Arrange
        using var files = new ExportTests.Fixture();
        using var transport = new TransportFixture((_, _, _) => Task.FromResult(TransportFixture.Response(body:
            "[{\"type\":\"Person search\",\"datetime\":\"2024-01-01T00:00:00Z\",\"outcome\":" + json + "}]")));
        var service = new StopSearchesIngestionService(transport.Client, files.Writer, files.Gate, TimeProvider.System);
        // Act
        var result = await service.IngestAsync(Request, default);
        // Assert
        Assert.Equal(Header + "Person search,2024-01-01T00:00:00.0000000+00:00,,,,,,," + expected + ",,,,,,,\r\n",
            await File.ReadAllTextAsync(Path.Combine(files.Root, result.Filename)));
    }

    [Theory]
    [InlineData("null-list")]
    [InlineData("null-row")]
    [InlineData("type")]
    [InlineData("timestamp")]
    [InlineData("coordinate")]
    public async Task StopSearches_InvalidClientRecord_ValidatesEntireListBeforeExport(string kind)
    {
        // Arrange
        var invalid = kind switch { "type" => Valid() with { Type = null }, "timestamp" => Valid() with { Datetime = "bad" },
            "coordinate" => Valid() with { Location = new() { Latitude = "Infinity" } }, _ => null! };
        var client = new Client { Records = kind == "null-list" ? null! : [Valid(), invalid] };
        var exporter = new ForcesTests.FakeExporter();
        var gate = new OperationLease();
        var service = new StopSearchesIngestionService(client, exporter, gate, TimeProvider.System);
        // Act
        var error = await Assert.ThrowsAsync<PoliceApiException>(() => service.IngestAsync(Request, default));
        // Assert
        Assert.Equal(PoliceApiFailure.InvalidPayload, error.Failure);
        Assert.Equal(0, exporter.Calls);
        using var free = gate.Acquire();
    }

    [Fact]
    public async Task StopSearches_RetrievalTimeout_Returns504WithoutPublication()
    {
        // Arrange
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new TransportFixture(async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TransportFixture.Response();
        }, new() { MaximumAttempts = 1 });
        var exporter = new ForcesTests.FakeExporter();
        var gate = new OperationLease();
        var service = new StopSearchesIngestionService(transport.Client, exporter, gate, TimeProvider.System);
        // Act
        var task = service.IngestAsync(Request, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        transport.Clock.Advance(TimeSpan.FromSeconds(30));
        var error = await Assert.ThrowsAsync<PoliceApiException>(() => task);
        // Assert
        Assert.Equal(504, error.StatusCode);
        Assert.Equal("upstream_timeout", error.Code);
        Assert.Equal(1, transport.Handler.Calls);
        Assert.Equal(0, exporter.Calls);
        using var free = gate.Acquire();
    }
    private static StopSearchDto Valid() => new() { Type = "Person search", Datetime = "2024-01-01T00:00:00Z" };
    private sealed class Client : IPoliceApiClient
    {
        public IReadOnlyList<StopSearchDto> Records { get; set; } = [Valid()];
        public Func<CancellationToken, Task<IReadOnlyList<StopSearchDto>>>? OnGet { get; set; }
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }
        public LocationMonth? Query { get; private set; }
        public Task<IReadOnlyList<StopSearchDto>> GetStopSearchesAsync(LocationMonth request, CancellationToken cancellationToken = default)
        { Calls++; Query = request; Token = cancellationToken; return OnGet?.Invoke(cancellationToken) ?? Task.FromResult(Records); }
        public Task<IReadOnlyList<CrimeDto>> GetCrimesAsync(LocationMonth request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ForceDto>> GetForcesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Service(IngestionResult result) : IStopSearchesIngestionService
    {
        public LocationMonthRequest? Request { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<IngestionResult> IngestAsync(LocationMonthRequest? request, CancellationToken cancellationToken)
        { Request = request; Token = cancellationToken; return Task.FromResult(result); }
    }
}
