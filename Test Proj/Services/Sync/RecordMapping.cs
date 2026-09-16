using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Test_Proj.Clients.PoliceApi;
using Test_Proj.Errors;
using Test_Proj.Validation;

namespace Test_Proj.Services.Sync;

public sealed record MappedRecord(string Key, string Data, string? SourceId = null);

public static class RecordMapping
{
    public static string Scope(LocationMonth request) => Hash(request.ToQueryString().Replace("=-0&", "=0&", StringComparison.Ordinal));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static IReadOnlyList<MappedRecord> Forces(IReadOnlyList<ForceDto> values) => Unique(values.Select(value =>
    {
        if (value is null || !value.IsValid() || value.Id!.Length > 160) throw Invalid();
        return new MappedRecord(value.Id, JsonSerializer.Serialize(value), value.Id);
    }));

    public static IReadOnlyList<MappedRecord> Crimes(IReadOnlyList<CrimeDto> values, string month) => Unique(values.Select(value =>
    {
        if (value is null || !value.IsValid(month)) throw Invalid();
        // Persistent IDs survive upstream reloads; numeric IDs are only a fallback within a month.
        var key = string.IsNullOrWhiteSpace(value.PersistentId)
            ? "id:" + value.Id!.Value.ToString(CultureInfo.InvariantCulture)
            : "persistent:" + value.PersistentId;
        if (key.Length > 160) throw Invalid();
        return new MappedRecord(key, JsonSerializer.Serialize(value), value.Id!.Value.ToString(CultureInfo.InvariantCulture));
    }));

    public static IReadOnlyList<MappedRecord> Stops(IReadOnlyList<StopSearchDto> values)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<MappedRecord>();
        foreach (var value in values)
        {
            if (value is null || !value.IsValid()) throw Invalid();
            var data = JsonSerializer.Serialize(value);
            var hash = Hash(data);
            var occurrence = occurrences.GetValueOrDefault(hash) + 1;
            occurrences[hash] = occurrence;
            // No upstream event ID exists. Preserve identical events rather than inventing identity or collapsing them.
            result.Add(new(hash + ":" + occurrence.ToString(CultureInfo.InvariantCulture), data));
        }
        return result;
    }

    private static IReadOnlyList<MappedRecord> Unique(IEnumerable<MappedRecord> values)
    {
        var result = new Dictionary<string, MappedRecord>(StringComparer.Ordinal);
        var sourceKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (result.TryGetValue(value.Key, out var previous) && previous.Data != value.Data) throw Invalid();
            if (value.SourceId is { } source)
            {
                if (sourceKeys.TryGetValue(source, out var key) && key != value.Key) throw Invalid();
                sourceKeys[source] = value.Key;
            }
            result[value.Key] = value;
        }
        return result.Values.ToArray();
    }
    private static PoliceApiException Invalid() => new(PoliceApiFailure.InvalidPayload);
}
