using System.Text.Json.Serialization;

namespace Test_Proj.Clients.PoliceApi;

public sealed record ForceDto
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    public bool IsValid() => !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(Name);
}
