namespace Test_Proj.Options;

public sealed class PoliceApiOptions
{
    public const string SectionName = "PoliceApi";
    public string BaseUrl { get; set; } = "https://data.police.uk/api/";
    public int AttemptTimeoutSeconds { get; set; } = 30;
    public int TotalOperationTimeoutSeconds { get; set; } = 120;
    public int MaximumAttempts { get; set; } = 3;
    public int MaximumResponseBytes { get; set; } = 32 * 1024 * 1024;
    public int MaximumRecords { get; set; } = 100_000;
}
