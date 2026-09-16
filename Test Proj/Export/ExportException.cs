namespace Test_Proj.Export;

public sealed class ExportException(string code, int statusCode = 500) : Exception("Export operation failed.")
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
