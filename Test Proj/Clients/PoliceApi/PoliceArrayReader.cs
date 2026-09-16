using System.Text.Json;
using Test_Proj.Errors;

namespace Test_Proj.Clients.PoliceApi;

/// <summary>Bounds bytes before parsing, and materializes at most the configured number of records.</summary>
public static class PoliceArrayReader
{
    public static async Task<IReadOnlyList<T>> ReadAsync<T>(HttpContent content, int maximumBytes,
        int maximumRecords, Func<T, bool> validate, CancellationToken cancellationToken) where T : class
    {
        if (maximumBytes < 1 || maximumRecords < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (content.Headers.ContentLength > maximumBytes) throw new PoliceApiException(PoliceApiFailure.ResponseLimit);
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(8192, maximumBytes)];
        while (true)
        {
            // Read one extra byte to distinguish an exact-size response from an oversized one.
            var count = await source.ReadAsync(chunk.AsMemory(0,
                (int)Math.Min(chunk.Length, maximumBytes - buffer.Length + 1)), cancellationToken);
            if (count == 0) break;
            if (buffer.Length + count > maximumBytes) throw new PoliceApiException(PoliceApiFailure.ResponseLimit);
            buffer.Write(chunk, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new PoliceApiException(PoliceApiFailure.InvalidPayload);
            if (document.RootElement.GetArrayLength() > maximumRecords)
                throw new PoliceApiException(PoliceApiFailure.ResponseLimit);
            var result = new List<T>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (element.ValueKind != JsonValueKind.Object)
                    throw new PoliceApiException(PoliceApiFailure.InvalidPayload);
                var record = element.Deserialize<T>();
                if (record is null || !validate(record)) throw new PoliceApiException(PoliceApiFailure.InvalidPayload);
                result.Add(record);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return result.AsReadOnly();
        }
        catch (JsonException) { throw new PoliceApiException(PoliceApiFailure.InvalidPayload); }
    }
}
