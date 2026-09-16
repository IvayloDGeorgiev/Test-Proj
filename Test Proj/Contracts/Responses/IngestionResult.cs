using System.Text.Json.Serialization;

namespace Test_Proj.Contracts.Responses;

public sealed record IngestionResult(bool Success, string Dataset,
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] int RecordCount,
    string Filename, DateTimeOffset CompletedAtUtc);
