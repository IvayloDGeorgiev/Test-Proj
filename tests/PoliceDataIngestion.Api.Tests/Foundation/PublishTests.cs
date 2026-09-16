using System.Diagnostics;

namespace PoliceDataIngestion.Api.Tests.Foundation;

public sealed class PublishTests
{
    [Fact]
    public async Task Publish_SyntheticLocalSettings_ExcludesLocalFileFromBuildAndPublish()
    {
        // Arrange: copy only the tracked project definition, never a real local settings file.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Test Proj.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        using var directory = new TemporaryDirectory();
        var project = Path.Combine(directory.Path, "PublishProbe.csproj");
        File.Copy(Path.Combine(root.FullName, "Test Proj", "Test Proj.csproj"), project);
        File.WriteAllText(Path.Combine(directory.Path, "Program.cs"), "var builder = WebApplication.CreateBuilder(args); builder.Build();");
        File.WriteAllText(Path.Combine(directory.Path, "appsettings.Local.json"), "{\"SyntheticPublishProbe\":true}");
        File.WriteAllText(Path.Combine(directory.Path, "appsettings.json"), "{\"SyntheticDefaultProbe\":true}");
        var output = Path.Combine(directory.Path, "published");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory.Path };
        foreach (var argument in new[] { "publish", project, "-o", output, "--nologo" }) start.ArgumentList.Add(argument);
        // Act
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        // Assert: positive controls prove publishing/building actually happened.
        Assert.True(process.ExitCode == 0, (await stdout) + (await stderr));
        Assert.True(File.Exists(Path.Combine(output, "PublishProbe.dll")));
        Assert.True(File.Exists(Path.Combine(output, "appsettings.json")));
        Assert.False(File.Exists(Path.Combine(output, "appsettings.Local.json")));
        var buildOutput = Path.Combine(directory.Path, "bin", "Release", "net10.0");
        Assert.True(File.Exists(Path.Combine(buildOutput, "PublishProbe.dll")));
        Assert.True(File.Exists(Path.Combine(buildOutput, "appsettings.json")));
        Assert.False(File.Exists(Path.Combine(buildOutput, "appsettings.Local.json")));
    }
}
