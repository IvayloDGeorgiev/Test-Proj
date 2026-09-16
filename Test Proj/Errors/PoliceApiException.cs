namespace Test_Proj.Errors;

public enum PoliceApiFailure { InvalidPayload, ResponseLimit, UpstreamFailure, RateLimited, Timeout, RetryBudget }

/// <summary>Safe classification only; never retains upstream bodies or transport exceptions.</summary>
public sealed class PoliceApiException(PoliceApiFailure failure) : Exception("Police data retrieval failed.")
{
    public PoliceApiFailure Failure { get; } = failure;
    public int StatusCode => Failure switch
    {
        PoliceApiFailure.RateLimited or PoliceApiFailure.RetryBudget => 503,
        PoliceApiFailure.Timeout => 504,
        _ => 502
    };
    public string Code => Failure switch
    {
        PoliceApiFailure.InvalidPayload => "upstream_invalid_payload",
        PoliceApiFailure.ResponseLimit => "upstream_limit_exceeded",
        PoliceApiFailure.RateLimited => "upstream_rate_limited",
        PoliceApiFailure.Timeout => "upstream_timeout",
        PoliceApiFailure.RetryBudget => "upstream_retry_budget",
        _ => "upstream_failure"
    };
}
