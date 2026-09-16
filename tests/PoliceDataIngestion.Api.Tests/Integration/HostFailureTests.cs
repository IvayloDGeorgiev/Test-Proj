using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Test_Proj.Export;
using static PoliceDataIngestion.Api.Tests.Integration.HostContractsTests;

namespace PoliceDataIngestion.Api.Tests.Integration;

public sealed class HostFailureTests
{
    public static IEnumerable<object[]> Failures()
    {
        foreach (var route in Routes)
        foreach (var environment in new[] { "Production", "Development" })
        foreach (var failure in new[] { "malformed", "permanent", "rate", "network", "unexpected", "bytes", "records", "publication" })
            yield return [route, environment, failure];
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Failure_RealPipeline_ReturnsSafeProblemPreservesOldFileAndReleasesLease(string route, string environment, string failure)
    {
        // Arrange
        await using var host = new IngestionHost((_, _, _) => failure switch
        {
            "network" => throw new HttpRequestException("synthetic-private-marker"),
            "unexpected" => throw new InvalidOperationException("synthetic-private-marker"),
            "malformed" => Task.FromResult(IngestionHost.Response("synthetic-private-marker")),
            "permanent" => Task.FromResult(IngestionHost.Response("synthetic-private-marker", 404)),
            "rate" => Task.FromResult(IngestionHost.Response("synthetic-private-marker", 429)),
            "records" => Task.FromResult(IngestionHost.Response("[{},{}]")),
            _ => Task.FromResult(IngestionHost.Response(Payload(route)))
        }) { EnvironmentName = environment };
        if (failure == "bytes") host.Settings["PoliceApi:MaximumResponseBytes"] = "2";
        if (failure == "records") host.Settings["PoliceApi:MaximumRecords"] = "1";
        if (failure == "publication") host.Files.BeforePublish = () => throw new IOException("synthetic-private-marker");
        Directory.CreateDirectory(host.OutputRoot);
        var destination = Path.Combine(host.OutputRoot, Filename(route));
        await File.WriteAllTextAsync(destination, "old-complete-export");
        using var client = host.Client();
        // Act
        using var response = await Post(client, route);
        // Assert
        var (status, code) = failure switch
        {
            "malformed" => (502, "upstream_invalid_payload"),
            "rate" => (503, "upstream_rate_limited"),
            "unexpected" => (500, "internal_error"),
            "publication" => (500, "export_failed"),
            "bytes" or "records" => (502, "upstream_limit_exceeded"),
            _ => (502, "upstream_failure")
        };
        await AssertProblem(response, status, code);
        Assert.Equal("old-complete-export", await File.ReadAllTextAsync(destination));
        Assert.Single(Directory.GetFiles(host.OutputRoot));
        Assert.Equal(1, host.Handler.Calls);
        Assert.Equal(0, host.Files.Publications);
        Assert.Equal(failure == "publication" ? 1 : 0, host.Files.Creates);
        using var lease = host.Services.GetRequiredService<OperationLease>().Acquire();
        AssertSafeLogs(host);
        Assert.Contains(host.Logs.Entries, e => e.Message.Contains(code));
    }

    [Fact]
    public async Task Cleanup_FailedAfterPublicationFailure_KeepsPrimaryErrorAndOldFileWithSafeWarning()
    {
        // Arrange
        await using var host = new IngestionHost();
        host.Files.FailCleanup = true;
        host.Files.BeforePublish = () => throw new IOException("synthetic-private-marker");
        Directory.CreateDirectory(host.OutputRoot);
        var path = Path.Combine(host.OutputRoot, "Forces.csv");
        await File.WriteAllTextAsync(path, "old");
        using var client = host.Client();
        // Act
        using var response = await Post(client, "forces");
        // Assert
        await AssertProblem(response, 500, "export_failed");
        Assert.Equal("old", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(host.OutputRoot, ".export-*.tmp"));
        Assert.Equal(1, host.Handler.Calls);
        Assert.Equal(1, host.Files.Creates);
        Assert.Contains(host.Logs.Entries, e => e.Message.Contains("export_cleanup_failed"));
        AssertSafeLogs(host);
        using var lease = host.Services.GetRequiredService<OperationLease>().Acquire();
    }

    [Fact]
    public async Task Retry_TransientThenSuccess_RetriesGetOnlyAndPublishesOnce()
    {
        // Arrange
        await using var host = new IngestionHost((call, _, _) => Task.FromResult(IngestionHost.Response(call < 3 ? "synthetic-private-marker" : Payload("forces"), call < 3 ? 503 : 200)));
        host.Settings["PoliceApi:MaximumAttempts"] = "3";
        using var client = host.Client();
        // Act
        var pending = Post(client, "forces");
        host.Clock.Advance(await host.Clock.WaitForBackoffAsync());
        host.Clock.Advance(await host.Clock.WaitForBackoffAsync());
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, host.Handler.Calls);
        Assert.Equal(1, host.Files.Creates);
        Assert.Equal(1, host.Files.Publications);
        Assert.Equal(Header("forces") + Row("forces"), await File.ReadAllTextAsync(Path.Combine(host.OutputRoot, "Forces.csv")));
        AssertSafeLogs(host);
    }

    [Fact]
    public async Task Retry_AfterBeyondBudget_Returns503WithoutEarlyRetryOrOutput()
    {
        // Arrange
        await using var host = new IngestionHost((_, _, _) =>
        {
            var response = IngestionHost.Response("synthetic-private-marker", 429);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(121));
            return Task.FromResult(response);
        });
        host.Settings["PoliceApi:MaximumAttempts"] = "3";
        using var client = host.Client();
        // Act
        using var response = await Post(client, "forces");
        // Assert
        await AssertProblem(response, 503, "upstream_retry_budget");
        Assert.Equal(1, host.Handler.Calls);
        Assert.Equal(0, host.Files.Creates);
        AssertSafeLogs(host);
    }

    [Fact]
    public async Task OpenApi_Development_GeneratedDocumentDescribesActualRoutesSchemasAndMediaTypes()
    {
        // Arrange
        await using var host = new IngestionHost { EnvironmentName = "Development" };
        using var client = host.Client();
        // Act
        using var response = await client.GetAsync("/openapi/v1.json");
        var body = await response.Content.ReadAsStringAsync();
        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = JsonDocument.Parse(body).RootElement;
        Assert.StartsWith("3.", document.GetProperty("openapi").GetString());
        var paths = document.GetProperty("paths");
        Assert.Equal(Routes.Select(r => "/api/ingestion/" + r).Order(), paths.EnumerateObject().Select(p => p.Name).Order());
        foreach (var route in Routes)
        {
            var operation = paths.GetProperty("/api/ingestion/" + route).GetProperty("post");
            var responses = operation.GetProperty("responses");
            Assert.Equal(["200", "400", "409", "413", "415", "500", "502", "503", "504"], responses.EnumerateObject().Select(p => p.Name).Order().ToArray());
            var success = Resolve(document, responses.GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
            Assert.Equal(["completedAtUtc", "dataset", "filename", "recordCount", "success"], success.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray());
            Assert.Equal("boolean", success.GetProperty("properties").GetProperty("success").GetProperty("type").GetString());
            Assert.Equal("integer", success.GetProperty("properties").GetProperty("recordCount").GetProperty("type").GetString());
            Assert.Equal("date-time", success.GetProperty("properties").GetProperty("completedAtUtc").GetProperty("format").GetString());
            foreach (var status in new[] { "400", "409", "413", "415", "500", "502", "503", "504" })
            {
                var content = responses.GetProperty(status).GetProperty("content");
                Assert.Equal("application/problem+json", Assert.Single(content.EnumerateObject()).Name);
                var schema = Resolve(document, content.GetProperty("application/problem+json").GetProperty("schema"));
                Assert.True(schema.GetProperty("properties").TryGetProperty("status", out _));
                Assert.True(schema.GetProperty("properties").TryGetProperty("title", out _));
                if (status == "400") Assert.True(schema.GetProperty("properties").TryGetProperty("errors", out _));
            }
            if (route == "forces") Assert.False(operation.TryGetProperty("requestBody", out _));
            else
            {
                Assert.True(operation.GetProperty("requestBody").GetProperty("required").GetBoolean());
                var request = Resolve(document, operation.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
                Assert.Equal(["latitude", "longitude", "month"], request.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray());
            }
        }
        Assert.Equal(0, host.Handler.Calls);
        Assert.False(Directory.Exists(host.OutputRoot));
    }

    [Fact]
    public async Task OpenApi_Production_IsNotExposed()
    {
        // Arrange
        await using var host = new IngestionHost();
        using var client = host.Client();
        // Act
        using var response = await client.GetAsync("/openapi/v1.json");
        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, host.Handler.Calls);
    }

    private static JsonElement Resolve(JsonElement document, JsonElement schema) => schema.TryGetProperty("$ref", out var reference)
        ? document.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/').Last()) : schema;
}
