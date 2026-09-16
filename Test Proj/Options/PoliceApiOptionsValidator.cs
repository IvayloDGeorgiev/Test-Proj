using Microsoft.Extensions.Options;

namespace Test_Proj.Options;

public sealed class PoliceApiOptionsValidator : IValidateOptions<PoliceApiOptions>
{
    public ValidateOptionsResult Validate(string? name, PoliceApiOptions options)
    {
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "data.police.uk" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath != "/api/")
            return ValidateOptionsResult.Fail("PoliceApi.BaseUrl must be the trusted HTTPS API base.");

        if (options.AttemptTimeoutSeconds is < 1 or > 30 ||
            options.TotalOperationTimeoutSeconds is < 1 or > 120 ||
            options.AttemptTimeoutSeconds > options.TotalOperationTimeoutSeconds ||
            options.MaximumAttempts is < 1 or > 3 ||
            options.MaximumResponseBytes is < 1 or > 33_554_432 ||
            options.MaximumRecords is < 1 or > 100_000)
            return ValidateOptionsResult.Fail("PoliceApi limits must be within the supported bounds.");

        return ValidateOptionsResult.Success;
    }
}
