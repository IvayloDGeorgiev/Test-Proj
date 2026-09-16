namespace Test_Proj.Options;

public sealed class ExportOptions
{
    public const string SectionName = "Export";
    public string? OutputRoot { get; set; }
    public int MaximumRequestBodyBytes { get; set; } = 4096;
    public int MaximumConcurrentOperations { get; set; } = 1;
    public int MaximumQueuedOperations { get; set; }
}
