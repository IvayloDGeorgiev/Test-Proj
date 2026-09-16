namespace Test_Proj.Clients.PoliceApi;

public interface IPoliceApiClient
{
    Task<IReadOnlyList<StopSearchDto>> GetStopSearchesAsync(Test_Proj.Validation.LocationMonth request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ForceDto>> GetForcesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CrimeDto>> GetCrimesAsync(Test_Proj.Validation.LocationMonth request, CancellationToken cancellationToken = default);
}
