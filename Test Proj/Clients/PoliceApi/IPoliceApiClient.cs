namespace Test_Proj.Clients.PoliceApi;

public interface IPoliceApiClient
{
    Task<IReadOnlyList<ForceDto>> GetForcesAsync(CancellationToken cancellationToken = default);
}
