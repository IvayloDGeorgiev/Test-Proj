namespace Test_Proj.Contracts.Responses;

public sealed record IngestionResult(bool Success, string Dataset, int RecordCount,
    string Filename, DateTimeOffset CompletedAtUtc);
