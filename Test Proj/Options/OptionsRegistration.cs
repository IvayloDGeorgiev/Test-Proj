using Microsoft.Extensions.Options;

namespace Test_Proj.Options;

public static class OptionsRegistration
{
    public static IServiceCollection AddIngestionOptions(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment, Func<string>? desktopDirectory = null)
    {
        services.AddSingleton<IValidateOptions<PoliceApiOptions>, PoliceApiOptionsValidator>();
        services.AddSingleton<IValidateOptions<ExportOptions>, ExportOptionsValidator>();
        services.AddOptions<PoliceApiOptions>().Bind(configuration.GetSection(PoliceApiOptions.SectionName))
            .ValidateOnStart();
        services.AddOptions<ExportOptions>().Bind(configuration.GetSection(ExportOptions.SectionName))
            .PostConfigure(options =>
            {
                if (string.IsNullOrWhiteSpace(options.OutputRoot) && environment.IsDevelopment())
                {
                    var desktop = (desktopDirectory ?? (() => Environment.GetFolderPath(
                        Environment.SpecialFolder.DesktopDirectory)))();
                    if (ExportOptionsValidator.IsSafeRoot(desktop) && Directory.Exists(desktop))
                        options.OutputRoot = Path.Combine(desktop, "PoliceDataIngestion");
                }
                if (ExportOptionsValidator.IsSafeRoot(options.OutputRoot))
                    options.OutputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.OutputRoot!));
            }).ValidateOnStart();
        return services;
    }
}
