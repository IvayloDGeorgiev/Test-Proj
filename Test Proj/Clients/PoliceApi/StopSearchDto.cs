using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Test_Proj.Clients.PoliceApi;

public sealed record StopSearchDto
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("datetime")] public string? Datetime { get; init; }
    [JsonPropertyName("age_range")] public string? AgeRange { get; init; }
    [JsonPropertyName("gender")] public string? Gender { get; init; }
    [JsonPropertyName("self_defined_ethnicity")] public string? SelfDefinedEthnicity { get; init; }
    [JsonPropertyName("officer_defined_ethnicity")] public string? OfficerDefinedEthnicity { get; init; }
    [JsonPropertyName("legislation")] public string? Legislation { get; init; }
    [JsonPropertyName("object_of_search")] public string? ObjectOfSearch { get; init; }
    [JsonPropertyName("outcome"), JsonConverter(typeof(StopSearchOutcomeConverter))] public string? Outcome { get; init; }
    [JsonPropertyName("involved_person")] public bool? InvolvedPerson { get; init; }
    [JsonPropertyName("operation")] public bool? Operation { get; init; }
    [JsonPropertyName("operation_name")] public string? OperationName { get; init; }
    [JsonPropertyName("location")] public CrimeLocationDto? Location { get; init; }

    public bool IsValid() => !string.IsNullOrWhiteSpace(Type) && TryTimestamp(out _) && (Location?.IsValid() ?? true);

    // Require an explicit ISO offset; never infer the machine's local timezone.
    public bool TryTimestamp(out DateTimeOffset timestamp)
    {
        timestamp = default;
        return Datetime is { Length: >= 20 and <= 33 } &&
            Regex.IsMatch(Datetime, @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant) &&
            DateTimeOffset.TryParse(Datetime, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
    }
}

// The official contract permits false for no outcome; other non-string shapes are invalid.
public sealed class StopSearchOutcomeConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.False => "false",
            _ => throw new JsonException("Invalid outcome shape.")
        };
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
