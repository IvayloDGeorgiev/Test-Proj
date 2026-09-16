using Microsoft.Extensions.Configuration.Json;

namespace Test_Proj.Options;

public static class ConfigurationExtensions
{
    public static void AddDevelopmentLocalConfiguration(
        this ConfigurationManager configuration, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment()) return;

        // Insert after the standard JSON providers, before secrets, environment and CLI.
        // Inserting (rather than appending) preserves the host's override precedence.
        var index = 0;
        for (var i = 0; i < configuration.Sources.Count; i++)
        {
            if (configuration.Sources[i] is JsonConfigurationSource json &&
                (json.Path == "appsettings.json" ||
                 json.Path == $"appsettings.{environment.EnvironmentName}.json"))
                index = i + 1;
        }

        configuration.Sources.Insert(index, new JsonConfigurationSource
        {
            FileProvider = environment.ContentRootFileProvider,
            Path = "appsettings.Local.json",
            Optional = true,
            ReloadOnChange = false
        });
    }
}
