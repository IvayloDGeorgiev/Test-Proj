using System.Globalization;

namespace Test_Proj.Validation;

/// <summary>Validated, immutable query values. Construction has no external side effects.</summary>
public sealed record LocationMonth
{
    public double Latitude { get; }
    public double Longitude { get; }
    public string Month { get; }

    private LocationMonth(double latitude, double longitude, string month) =>
        (Latitude, Longitude, Month) = (latitude, longitude, month);

    public static LocationMonth Create(double? latitude, double? longitude, string? month)
    {
        var errors = new Dictionary<string, string[]>();
        if (latitude is null || !double.IsFinite(latitude.Value) || latitude is < -90 or > 90)
            errors["latitude"] = ["Supply a finite latitude between -90 and 90."];
        if (longitude is null || !double.IsFinite(longitude.Value) || longitude is < -180 or > 180)
            errors["longitude"] = ["Supply a finite longitude between -180 and 180."];
        if (month is null || month.Length != 7 || month[4] != '-' ||
            month.Where((_, i) => i != 4).Any(c => c is < '0' or > '9') ||
            !DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
            errors["month"] = ["Supply a calendar month in ASCII YYYY-MM format."];
        if (errors.Count != 0) throw new RequestValidationException(errors);
        return new(latitude!.Value, longitude!.Value, month!);
    }

    public string ToQueryString() => string.Create(CultureInfo.InvariantCulture,
        $"lat={Latitude:R}&lng={Longitude:R}&date={Month}");
}

public sealed class RequestValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("Request validation failed.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}
