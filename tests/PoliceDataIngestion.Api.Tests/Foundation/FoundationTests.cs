using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Test_Proj.Options;

namespace PoliceDataIngestion.Api.Tests.Foundation;

public sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "police-foundation-" + Guid.NewGuid());
    public TemporaryDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("Development", "local")]
    [InlineData("Production", "base")]
    [InlineData("Staging", "base")]
    public async Task Configuration_SyntheticLocalFile_RespectsEnvironmentAndRegistrationTiming(string environment, string expected)
    {
        // Arrange: every configuration file is synthetic, in an isolated temporary root.
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "appsettings.json"), "{\"FoundationValue\":\"base\"}");
        File.WriteAllText(Path.Combine(directory.Path, "appsettings.Local.json"), "{\"FoundationValue\":\"local\"}");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment, ContentRootPath = directory.Path, Args = [] });
        // Act: use the same ordering as Program, including a service that captures configuration immediately.
        builder.Configuration.AddDevelopmentLocalConfiguration(builder.Environment);
        builder.Services.AddSingleton(new CapturedValue(builder.Configuration["FoundationValue"]));
        await using var app = builder.Build();
        // Assert
        Assert.Equal(expected, app.Services.GetRequiredService<CapturedValue>().Value);
        Assert.Equal(expected, app.Configuration["FoundationValue"]);
    }

    [Theory]
    [InlineData(false, "environment")]
    [InlineData(true, "command-line")]
    public void Configuration_LocalFile_PreservesEnvironmentAndCommandLinePriority(bool withCommandLine, string expected)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "appsettings.Local.json"), "{\"Probe\":\"local\"}");
        var prefix = "FOUNDATION_" + Guid.NewGuid().ToString("N") + "_";
        Environment.SetEnvironmentVariable(prefix + "Probe", "environment");
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ContentRootPath = directory.Path, Args = [] });
            builder.Configuration.AddEnvironmentVariables(prefix);
            if (withCommandLine) builder.Configuration.AddCommandLine(["--Probe=command-line"]);
            // Act
            builder.Configuration.AddDevelopmentLocalConfiguration(builder.Environment);
            // Assert
            Assert.Equal(expected, builder.Configuration["Probe"]);
            (builder.Configuration as IDisposable).Dispose();
        }
        finally { Environment.SetEnvironmentVariable(prefix + "Probe", null); }
    }

    [Fact]
    public void Configuration_MissingOptionalLocalFile_KeepsExistingConfiguration()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ContentRootPath = directory.Path, Args = ["--Probe=retained"] });
        // Act
        builder.Configuration.AddDevelopmentLocalConfiguration(builder.Environment);
        // Assert
        Assert.Equal("retained", builder.Configuration["Probe"]);
        Assert.False(File.Exists(Path.Combine(directory.Path, "appsettings.Local.json")));
        (builder.Configuration as IDisposable).Dispose();
    }

    private sealed record CapturedValue(string? Value);
}

public sealed class OptionsTests
{
    [Theory]
    [InlineData("http://data.police.uk/api/")]
    [InlineData("https://evil.example/api/")]
    [InlineData("https://data.police.uk.evil.example/api/")]
    [InlineData("https://data.police.uk:444/api/")]
    [InlineData("https://synthetic@data.police.uk/api/")]
    [InlineData("https://data.police.uk/api/?x=1")]
    [InlineData("https://data.police.uk/api/#fragment")]
    [InlineData("https://data.police.uk/other/")]
    [InlineData("")]
    public void PoliceOptions_UnsafeOrigin_FailsWithoutEchoingValue(string origin)
    {
        // Arrange
        var options = new PoliceApiOptions { BaseUrl = origin };
        // Act
        var result = new PoliceApiOptionsValidator().Validate(null, options);
        // Assert
        Assert.True(result.Failed);
        Assert.Equal("PoliceApi.BaseUrl must be the trusted HTTPS API base.", result.FailureMessage);
    }

    [Theory]
    [InlineData(nameof(PoliceApiOptions.AttemptTimeoutSeconds), 0)]
    [InlineData(nameof(PoliceApiOptions.AttemptTimeoutSeconds), 31)]
    [InlineData(nameof(PoliceApiOptions.TotalOperationTimeoutSeconds), 0)]
    [InlineData(nameof(PoliceApiOptions.TotalOperationTimeoutSeconds), 121)]
    [InlineData(nameof(PoliceApiOptions.MaximumAttempts), 0)]
    [InlineData(nameof(PoliceApiOptions.MaximumAttempts), 4)]
    [InlineData(nameof(PoliceApiOptions.MaximumResponseBytes), 0)]
    [InlineData(nameof(PoliceApiOptions.MaximumResponseBytes), 33554433)]
    [InlineData(nameof(PoliceApiOptions.MaximumRecords), 0)]
    [InlineData(nameof(PoliceApiOptions.MaximumRecords), 100001)]
    public void PoliceOptions_UnsafeLimits_Fail(string property, int value)
    {
        // Arrange
        var options = new PoliceApiOptions();
        typeof(PoliceApiOptions).GetProperty(property)!.SetValue(options, value);
        // Act
        var result = new PoliceApiOptionsValidator().Validate(null, options);
        // Assert
        Assert.True(result.Failed);
    }

    [Fact]
    public void PoliceOptions_AttemptExceedsTotalBudget_Fails()
    {
        // Arrange
        var options = new PoliceApiOptions { TotalOperationTimeoutSeconds = 20 };
        // Act
        var result = new PoliceApiOptionsValidator().Validate(null, options);
        // Assert
        Assert.True(result.Failed);
    }

    [Fact]
    public void PoliceOptions_DefaultsAndMinimumLimits_AreAccepted()
    {
        // Arrange
        var validator = new PoliceApiOptionsValidator();
        var minimum = new PoliceApiOptions { AttemptTimeoutSeconds = 1, TotalOperationTimeoutSeconds = 1, MaximumAttempts = 1, MaximumResponseBytes = 1, MaximumRecords = 1 };
        // Act / Assert
        Assert.True(validator.Validate(null, new()).Succeeded);
        Assert.True(validator.Validate(null, minimum).Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("../escape")]
    [InlineData("\\\\server\\share\\exports")]
    [InlineData("//server/share/exports")]
    public void ExportOptions_UnsafeRoot_Fails(string? root)
    {
        // Arrange
        var options = new ExportOptions { OutputRoot = root };
        // Act
        var result = new ExportOptionsValidator().Validate(null, options);
        // Assert
        Assert.True(result.Failed);
    }

    [Fact]
    public void ExportOptions_TraversalFileAndFilesystemRoot_Fail()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var file = Path.Combine(directory.Path, "file");
        File.WriteAllText(file, "synthetic");
        var validator = new ExportOptionsValidator();
        // Act / Assert
        foreach (var root in new[] { Path.Combine(directory.Path, "..", "escape"), file, Path.GetPathRoot(directory.Path)! })
            Assert.True(validator.Validate(null, new ExportOptions { OutputRoot = root }).Failed);
    }

    [Theory]
    [InlineData(nameof(ExportOptions.MaximumRequestBodyBytes), 0)]
    [InlineData(nameof(ExportOptions.MaximumRequestBodyBytes), 4097)]
    [InlineData(nameof(ExportOptions.MaximumConcurrentOperations), 0)]
    [InlineData(nameof(ExportOptions.MaximumConcurrentOperations), 2)]
    [InlineData(nameof(ExportOptions.MaximumQueuedOperations), -1)]
    [InlineData(nameof(ExportOptions.MaximumQueuedOperations), 1)]
    public void ExportOptions_UnsafeLimits_Fail(string property, int value)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var options = new ExportOptions { OutputRoot = directory.Path };
        typeof(ExportOptions).GetProperty(property)!.SetValue(options, value);
        // Act
        var result = new ExportOptionsValidator().Validate(null, options);
        // Assert
        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData("Development", true, true)]
    [InlineData("Development", false, false)]
    [InlineData("Production", true, false)]
    public async Task Options_DesktopFallback_OnlyUsesAvailableDevelopmentDesktop(string environment, bool available, bool succeeds)
    {
        // Arrange: injected directory is synthetic, never the real Desktop.
        using var directory = new TemporaryDirectory();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment, ContentRootPath = directory.Path, Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Services.AddIngestionOptions(builder.Configuration, builder.Environment, () => available ? directory.Path : "");
        using var host = builder.Build();
        // Act / Assert: startup itself must validate, before any consumer resolves options.
        if (succeeds)
        {
            await host.StartAsync();
            Assert.Equal(Path.Combine(directory.Path, "PoliceDataIngestion"), host.Services.GetRequiredService<IOptions<ExportOptions>>().Value.OutputRoot);
            await host.StopAsync();
        }
        else
        {
            var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
            Assert.Equal(typeof(ExportOptions), error.OptionsType);
        }
    }

    [Theory]
    [InlineData("PoliceApi:BaseUrl", "http://unsafe.example/api/")]
    [InlineData("PoliceApi:MaximumAttempts", "4")]
    [InlineData("Export:MaximumRequestBodyBytes", "4097")]
    public async Task Options_InvalidBoundConfiguration_FailsStartup(string key, string value)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Production", ContentRootPath = directory.Path, Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Export:OutputRoot"] = directory.Path, [key] = value });
        builder.Services.AddIngestionOptions(builder.Configuration, builder.Environment);
        using var host = builder.Build();
        // Act
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        // Assert
        Assert.NotEmpty(error.Failures);
        Assert.DoesNotContain("unsafe.example", error.Message);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM1")]
    [InlineData("LPT9.csv")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("bad:name")]
    public void ExportOptions_UnsafeWindowsPathComponent_Fails(string component)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var options = new ExportOptions { OutputRoot = Path.Combine(directory.Path, component) };
        // Act
        var result = new ExportOptionsValidator().Validate(null, options);
        // Assert: these cases express the supported Windows filesystem contract.
        Assert.True(result.Failed);
    }

    [Fact]
    public async Task Options_NonexistentDesktop_FailsStartupWithoutCreatingDirectory()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var missing = Path.Combine(directory.Path, "missing-desktop");
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development", ContentRootPath = directory.Path, Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Services.AddIngestionOptions(builder.Configuration, builder.Environment, () => missing);
        using var host = builder.Build();
        // Act
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        // Assert
        Assert.Equal(typeof(ExportOptions), error.OptionsType);
        Assert.False(Directory.Exists(missing));
    }
    [Fact]
    public async Task Options_ExplicitRootInProduction_StartsAndNormalizesRoot()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Production", ContentRootPath = directory.Path, Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Export:OutputRoot"] = directory.Path + Path.DirectorySeparatorChar });
        builder.Services.AddIngestionOptions(builder.Configuration, builder.Environment, () => throw new InvalidOperationException("Desktop must not be consulted"));
        using var host = builder.Build();
        // Act
        await host.StartAsync();
        // Assert
        Assert.Equal(directory.Path, host.Services.GetRequiredService<IOptions<ExportOptions>>().Value.OutputRoot);
        await host.StopAsync();
    }
}
