namespace Test_Proj.Clients.PoliceApi;

public interface IRetryJitter { double NextFraction(); }

public sealed class RetryJitter : IRetryJitter
{
    public double NextFraction() => Random.Shared.NextDouble();
}
