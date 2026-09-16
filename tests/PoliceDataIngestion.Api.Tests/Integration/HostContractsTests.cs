using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Test_Proj.Options;

namespace PoliceDataIngestion.Api.Tests.Integration;

public sealed class HostContractsTests
{
    internal const string RequestJson = """{"latitude":53.8008,"longitude":-1.5491,"month":"2024-01"}""";
    internal static readonly string[] Routes = ["forces", "crimes", "stop-searches"];
    internal static string Filename(string route) => route switch
    {
        "forces" => "Forces.csv", "crimes" => "Crimes_2024-01.csv", _ => "StopSearches_2024-01.csv"
    };
    internal static string Header(string route) => route switch
    {
        "forces" => "id,name\r\n",
        "crimes" => "id,persistent_id,category,month,latitude,longitude,street_id,street_name,location_type,context,outcome_category,outcome_date\r\n",
        _ => "type,datetime,age_range,gender,self_defined_ethnicity,officer_defined_ethnicity,legislation,object_of_search,outcome,involved_person,operation,operation_name,latitude,longitude,street_id,street_name\r\n"
    };
    internal static string Payload(string route) => route switch
    {
        "forces" => """[{"id":"test","name":" =SUM(1,2)"}]""",
        "crimes" => """[{"id":1,"category":"burglary","month":"2024-01","location":{"latitude":"53.8008","longitude":"-1.5491","street":{"id":7,"name":"Café, Lane"}}}]""",
        _ => """[{"type":"Person search","datetime":"2024-01-01T10:00:00+01:00","outcome":false,"involved_person":true,"operation":false}]"""
    };
    internal static string Row(string route) => route switch
    {
        "forces" => "test,\"' =SUM(1,2)\"\r\n",
        "crimes" => "1,,burglary,2024-01,53.8008,-1.5491,7,\"Café, Lane\",,,,\r\n",
        _ => "Person search,2024-01-01T10:00:00.0000000+01:00,,,,,,,false,true,false,,,,,\r\n"
    };
    internal static Task<HttpResponseMessage> Post(HttpClient client, string route, CancellationToken token = default) =>
        client.PostAsync("/api/ingestion/" + route, route == "forces" ? null : new StringContent(RequestJson, Encoding.UTF8, "application/json"), token);

    [Theory]
    [InlineData("forces")]
    [InlineData("crimes")]
    [InlineData("stop-searches")]
    public async Task Post_ValidThenEmpty_PublishesExactCsvAndReplacesBeforeSuccess(string route)
    {
        // Arrange: actual host/client/mapping/export; only remote HTTP is scripted.
        await using var host = new IngestionHost((call, _, _) => Task.FromResult(IngestionHost.Response(call == 1 ? Payload(route) : "[]")));
        using var client = host.Client();
        var originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            // Act
            using var response = await Post(client, route);
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(["completedAtUtc", "dataset", "filename", "recordCount", "success"], json.EnumerateObject().Select(p => p.Name).Order().ToArray());
            Assert.True(json.GetProperty("success").GetBoolean());
            Assert.Equal(route, json.GetProperty("dataset").GetString());
            Assert.Equal(1, json.GetProperty("recordCount").GetInt32());
            Assert.Equal(Filename(route), json.GetProperty("filename").GetString());
            Assert.Equal(host.Clock.GetUtcNow(), json.GetProperty("completedAtUtc").GetDateTimeOffset());
            Assert.Equal(Encoding.UTF8.GetBytes(Header(route) + Row(route)), await File.ReadAllBytesAsync(Path.Combine(host.OutputRoot, Filename(route))));
            Assert.Equal(1, host.Files.Publications);
            var uri = Assert.Single(host.Handler.Uris);
            Assert.Equal(route switch { "forces" => "https://data.police.uk/api/forces", "crimes" => "https://data.police.uk/api/crimes-street/all-crime?lat=53.8008&lng=-1.5491&date=2024-01", _ => "https://data.police.uk/api/stops-street?lat=53.8008&lng=-1.5491&date=2024-01" }, uri.AbsoluteUri);
            using var empty = await Post(client, route);
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
            Assert.Equal(0, JsonDocument.Parse(await empty.Content.ReadAsStringAsync()).RootElement.GetProperty("recordCount").GetInt32());
            Assert.Equal(Header(route), await File.ReadAllTextAsync(Path.Combine(host.OutputRoot, Filename(route))));
            Assert.Equal(2, host.Files.Publications);
            Assert.Single(Directory.GetFiles(host.OutputRoot));
            Assert.Contains(host.Logs.Entries, e => e.Message.Contains("published") && e.Message.Contains(route));
            Assert.DoesNotContain(host.Logs.Entries, e => e.Message.Contains("53.8008") || e.Message.Contains(host.OutputRoot) || e.Message.Contains("SUM("));
            AssertSafeLogs(host);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    public static IEnumerable<object[]> InvalidRequests()
    {
        foreach (var route in new[] { "crimes", "stop-searches" })
        foreach (var body in new[] { "", "null", "{}", "[]", "{", "{\"latitude\":\"synthetic-private-marker\"}", "{\"latitude\":1e999,\"longitude\":0,\"month\":\"2024-01\"}", "{\"latitude\":91,\"longitude\":0,\"month\":\"2024-01\"}", "{\"latitude\":0,\"longitude\":-181,\"month\":\"2024-01\"}", "{\"latitude\":0,\"longitude\":0,\"month\":\"2024-13\"}", "{\"latitude\":0,\"longitude\":0,\"month\":\"٢٠٢٤-01\"}" })
            yield return [route, body];
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Binding_InvalidJsonOrValues_ReturnsSafe400WithoutSideEffects(string route, string body)
    {
        // Arrange
        await using var host = new IngestionHost();
        using var client = host.Client();
        // Act
        using var response = await client.PostAsync("/api/ingestion/" + route, new StringContent(body, Encoding.UTF8, "application/json"));
        // Assert
        var problem = await AssertProblem(response, 400, "validation_failed");
        Assert.NotEmpty(problem.GetProperty("errors").EnumerateObject());
        Assert.Equal(0, host.Handler.Calls);
        Assert.Equal(0, host.Files.Creates);
        Assert.False(Directory.Exists(host.OutputRoot));
        AssertSafeLogs(host);
    }

    [Theory]
    [InlineData("crimes")]
    [InlineData("stop-searches")]
    public async Task Binding_UnsupportedMediaType_ReturnsSanitized415(string route)
    {
        // Arrange
        await using var host = new IngestionHost();
        using var client = host.Client();
        // Act
        using var response = await client.PostAsync("/api/ingestion/" + route, new StringContent("synthetic-private-marker", Encoding.UTF8, "text/plain"));
        // Assert
        await AssertProblem(response, 415, "unsupported_media_type");
        Assert.Equal(0, host.Handler.Calls);
        Assert.Equal(0, host.Files.Creates);
        AssertSafeLogs(host);
    }

    [Theory]
    [InlineData("forces", false)]
    [InlineData("crimes", false)]
    [InlineData("stop-searches", false)]
    [InlineData("forces", true)]
    [InlineData("crimes", true)]
    [InlineData("stop-searches", true)]
    public async Task Input_KnownAndUnknownLengthOverLimit_Returns413BeforeWork(string route, bool unknown)
    {
        // Arrange
        await using var host = new IngestionHost();
        using var client = host.Client();
        using HttpContent body = unknown ? new UnknownLengthContent(new string('x', 4097)) : new StringContent(new string('x', 4097));
        // Act
        using var response = await client.PostAsync("/api/ingestion/" + route, body);
        // Assert
        await AssertProblem(response, 413, "request_body_too_large");
        Assert.Equal(0, host.Handler.Calls);
        Assert.Equal(0, host.Files.Creates);
    }

    [Theory]
    [InlineData("crimes", -90d, -180d)]
    [InlineData("stop-searches", 90d, 180d)]
    public async Task Binding_InclusiveBoundsAndExactInputLimit_AcceptsValidJson(string route, double latitude, double longitude)
    {
        // Arrange
        await using var host = new IngestionHost();
        using var client = host.Client();
        var body = JsonSerializer.Serialize(new { latitude, longitude, month = "0001-01" }).PadRight(4096);
        // Act
        using var response = await client.PostAsync("/api/ingestion/" + route, new StringContent(body, Encoding.UTF8, "application/json"));
        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, host.Handler.Calls);
        Assert.Equal(1, host.Files.Publications);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task Configuration_IsolatedHost_UsesOnlySyntheticRootAndExpectedEnvironment(string environment)
    {
        // Arrange
        await using var host = new IngestionHost { EnvironmentName = environment };
        using var client = host.Client();
        // Act
        using var response = await Post(client, "forces");
        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = host.Services.GetRequiredService<IHostEnvironment>();
        Assert.Equal(environment, actual.EnvironmentName);
        Assert.Equal(host.ContentRoot, actual.ContentRootPath);
        Assert.Equal(host.OutputRoot, host.Services.GetRequiredService<IOptions<ExportOptions>>().Value.OutputRoot);
    }

    internal static async Task<JsonElement> AssertProblem(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("synthetic-private-marker", body);
        Assert.DoesNotContain("System.", body);
        var json = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal(status, json.GetProperty("status").GetInt32());
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("traceId").GetString()));
        Assert.Equal("about:blank", json.GetProperty("type").GetString());
        Assert.False(json.TryGetProperty("exception", out _));
        return json;
    }

    internal static void AssertSafeLogs(IngestionHost host)
    {
        Assert.DoesNotContain(host.Logs.Entries, e => e.Message.Contains("synthetic-private-marker") || e.Message.Contains(host.OutputRoot));
        Assert.All(host.Logs.Entries, e => Assert.Null(e.Exception));
    }

    private sealed class UnknownLengthContent(string value) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(Encoding.UTF8.GetBytes(value)).AsTask();
    }
}
