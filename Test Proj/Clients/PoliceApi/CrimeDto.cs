using System.Globalization;
using System.Text.Json.Serialization;

namespace Test_Proj.Clients.PoliceApi;

public sealed record CrimeDto
{
    [JsonPropertyName("id")] public long? Id { get; init; }
    [JsonPropertyName("persistent_id")] public string? PersistentId { get; init; }
    [JsonPropertyName("category")] public string? Category { get; init; }
    [JsonPropertyName("month")] public string? Month { get; init; }
    [JsonPropertyName("location")] public CrimeLocationDto? Location { get; init; }
    [JsonPropertyName("location_type")] public string? LocationType { get; init; }
    [JsonPropertyName("context")] public string? Context { get; init; }
    [JsonPropertyName("outcome_status")] public CrimeOutcomeDto? Outcome { get; init; }

    public bool IsValid(string requestedMonth) => Id is > 0 && !string.IsNullOrWhiteSpace(Category) &&
        Month == requestedMonth && (Location?.IsValid() ?? true);
}

public sealed record CrimeLocationDto
{
    [JsonPropertyName("latitude")] public string? Latitude { get; init; }
    [JsonPropertyName("longitude")] public string? Longitude { get; init; }
    [JsonPropertyName("street")] public CrimeStreetDto? Street { get; init; }

    public bool IsValid() => ValidCoordinate(Latitude, 90) && ValidCoordinate(Longitude, 180) &&
        (Street?.Id is null or >= 0);
    private static bool ValidCoordinate(string? value, double limit) => value is null ||
        (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
         double.IsFinite(number) && number >= -limit && number <= limit);
    // Only called after validation; null optional coordinates stay empty.
    public static double? Coordinate(string? value) => value is null ? null :
        double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
}

public sealed record CrimeStreetDto
{
    [JsonPropertyName("id")] public long? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
}

public sealed record CrimeOutcomeDto
{
    [JsonPropertyName("category")] public string? Category { get; init; }
    [JsonPropertyName("date")] public string? Date { get; init; }
}
