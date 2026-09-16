using System.Globalization;

namespace Test_Proj.Export;

/// <summary>Text is always formula-protected; only typed numbers bypass text protection.</summary>
public readonly struct CsvCell
{
    private readonly string? value;
    private CsvCell(string? value) => this.value = value;

    public static CsvCell Text(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            var first = 0;
            while (first < value.Length && (char.IsWhiteSpace(value[first]) || char.IsControl(value[first]))) first++;
            if (value[0] is '\t' or '\r' or '\n' ||
                (first < value.Length && value[first] is '=' or '+' or '-' or '@')) value = "'" + value;
        }
        return new(value);
    }

    public static CsvCell Number(double? value)
    {
        if (value is { } number && !double.IsFinite(number))
            throw new ArgumentOutOfRangeException(nameof(value), "CSV numbers must be finite.");
        return new(value?.ToString("R", CultureInfo.InvariantCulture));
    }

    public static CsvCell Integer(long? value) => new(value?.ToString(CultureInfo.InvariantCulture));
    public static CsvCell Boolean(bool? value) => new(value is null ? null : value.Value ? "true" : "false");
    public static CsvCell Timestamp(DateTimeOffset? value) => new(value?.ToString("O", CultureInfo.InvariantCulture));

    internal string Encode()
    {
        var text = value ?? "";
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}
