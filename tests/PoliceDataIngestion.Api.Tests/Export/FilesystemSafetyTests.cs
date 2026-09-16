using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Test_Proj.Export;
using Test_Proj.Options;
using static PoliceDataIngestion.Api.Tests.Export.ExportTests;

namespace PoliceDataIngestion.Api.Tests.Export;

public sealed class FilesystemSafetyTests
{
    [Fact]
    public async Task Export_ExistingReader_SeesOldCompleteFileWhileNewReadersSeeReplacement()
    {
        // Arrange
        using var fixture = new Fixture();
        using var lease = fixture.Gate.Acquire();
        await File.WriteAllTextAsync(fixture.Final, "old complete");
        using var oldHandle = new FileStream(fixture.Final, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(oldHandle);
        // Act
        await fixture.Writer.ExportAsync(ExportFile.Forces, [[CsvCell.Text("new"), CsvCell.Text("complete")]], lease, default);
        // Assert
        Assert.Equal("old complete", await reader.ReadToEndAsync());
        Assert.Equal("id,name\r\nnew,complete\r\n", await File.ReadAllTextAsync(fixture.Final));
        Assert.Single(Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task Export_NewFile_IsAbsentUntilCompleteTemporaryHasClosed()
    {
        // Arrange
        var operations = new FaultOperations();
        using var fixture = new Fixture(operations);
        using var lease = fixture.Gate.Acquire();
        var observed = false;
        operations.BeforePublish = () =>
        {
            observed = true;
            Assert.False(File.Exists(fixture.Final));
            Assert.Equal("id,name\r\n", File.ReadAllText(Assert.Single(operations.TemporaryPaths)));
        };
        // Act
        await fixture.Writer.ExportAsync(ExportFile.Forces, [], lease, default);
        // Assert
        Assert.True(observed);
        Assert.Equal("id,name\r\n", await File.ReadAllTextAsync(fixture.Final));
    }

    [Fact]
    public async Task Export_RootBecomesFile_RejectsWithoutChangingIt()
    {
        // Arrange
        using var fixture = new Fixture();
        Directory.Delete(fixture.Root);
        await File.WriteAllTextAsync(fixture.Root, "keep this file");
        using var lease = fixture.Gate.Acquire();
        try
        {
            // Act / Assert
            Assert.Equal("unsafe_export_path", (await Assert.ThrowsAsync<ExportException>(() =>
                fixture.Writer.ExportAsync(ExportFile.Forces, [], lease, default))).Code);
            Assert.Equal("keep this file", await File.ReadAllTextAsync(fixture.Root));
        }
        finally { File.Delete(fixture.Root); }
    }

    [Fact]
    public async Task Export_TemporaryFiles_AreUniqueSameDirectoryAndOldFileRemainsUntilPublication()
    {
        // Arrange
        var operations = new FaultOperations();
        using var fixture = new Fixture(operations);
        using var lease = fixture.Gate.Acquire();
        await File.WriteAllTextAsync(fixture.Final, "old complete");
        var observed = false;
        operations.BeforePublish = () =>
        {
            observed = true;
            Assert.Equal("old complete", File.ReadAllText(fixture.Final));
            var temporary = operations.TemporaryPaths.Last();
            Assert.Equal(fixture.Root, Path.GetDirectoryName(temporary));
            Assert.Equal("id,name\r\nnew,complete\r\n", File.ReadAllText(temporary));
            // The handle is closed before publication.
            using var exclusive = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.None);
        };
        // Act
        await fixture.Writer.ExportAsync(ExportFile.Forces, [[CsvCell.Text("new"), CsvCell.Text("complete")]], lease, default);
        operations.BeforePublish = null;
        await fixture.Writer.ExportAsync(ExportFile.Forces, [], lease, default);
        // Assert
        Assert.True(observed);
        Assert.Equal(2, operations.TemporaryPaths.Distinct().Count());
        Assert.All(operations.TemporaryPaths, path => Assert.False(File.Exists(path)));
        Assert.Equal("id,name\r\n", await File.ReadAllTextAsync(fixture.Final));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_RootReplacedByJunctionAfterStartup_RejectsBeforeWrite(bool dangling)
    {
        // Arrange
        using var fixture = new Fixture();
        using var outside = new Fixture();
        Directory.Delete(fixture.Root);
        CreateJunction(fixture.Root, outside.Root);
        if (dangling) Directory.Delete(outside.Root);
        using var lease = fixture.Gate.Acquire();
        try
        {
            // Act
            var error = await Assert.ThrowsAsync<ExportException>(() => fixture.Writer.ExportAsync(ExportFile.Forces, [], lease, default));
            // Assert
            Assert.Equal("unsafe_export_path", error.Code);
            Assert.False(File.Exists(outside.Final));
        }
        finally { Directory.Delete(fixture.Root); }
    }

    [Fact]
    public async Task Export_AncestorJunction_RejectsAtWriteTime()
    {
        // Arrange
        using var container = new Fixture();
        using var outside = new Fixture();
        var ancestor = Path.Combine(container.Root, "ancestor");
        var nested = Path.Combine(ancestor, "nested");
        Directory.CreateDirectory(nested);
        var gate = new OperationLease();
        var writer = new CsvExporter(Options.Create(new ExportOptions { OutputRoot = nested }), gate,
            new AtomicFileOperations(), NullLogger<CsvExporter>.Instance);
        Directory.Delete(nested);
        Directory.Delete(ancestor);
        CreateJunction(ancestor, outside.Root);
        using var lease = gate.Acquire();
        try
        {
            // Act / Assert
            Assert.Equal("unsafe_export_path", (await Assert.ThrowsAsync<ExportException>(() =>
                writer.ExportAsync(ExportFile.Forces, [], lease, default))).Code);
            Assert.Empty(Directory.GetFileSystemEntries(outside.Root));
        }
        finally { Directory.Delete(ancestor); }
    }

    [Fact]
    public async Task Export_DestinationJunction_RejectsWithoutTouchingTarget()
    {
        // Arrange
        using var fixture = new Fixture();
        using var outside = new Fixture();
        CreateJunction(fixture.Final, outside.Root);
        using var lease = fixture.Gate.Acquire();
        try
        {
            // Act / Assert
            Assert.Equal("unsafe_export_path", (await Assert.ThrowsAsync<ExportException>(() =>
                fixture.Writer.ExportAsync(ExportFile.Forces, [], lease, default))).Code);
            Assert.Empty(Directory.GetFiles(outside.Root));
            Assert.Single(Directory.GetFileSystemEntries(fixture.Root));
        }
        finally { Directory.Delete(fixture.Final); }
    }

    [Fact]
    public async Task Export_DestinationChangedDuringRows_RechecksBeforePublicationAndCleansTemp()
    {
        // Arrange
        using var fixture = new Fixture();
        using var outside = new Fixture();
        using var lease = fixture.Gate.Acquire();
        IEnumerable<IReadOnlyList<CsvCell>> Rows()
        {
            CreateJunction(fixture.Final, outside.Root);
            yield return [CsvCell.Text("new"), CsvCell.Text("row")];
        }
        try
        {
            // Act
            var error = await Assert.ThrowsAsync<ExportException>(() => fixture.Writer.ExportAsync(ExportFile.Forces, Rows(), lease, default));
            // Assert
            Assert.Equal("unsafe_export_path", error.Code);
            Assert.Empty(Directory.GetFiles(fixture.Root));
            Assert.Empty(Directory.GetFileSystemEntries(outside.Root));
        }
        finally { Directory.Delete(fixture.Final); }
    }

    [Fact]
    public async Task Export_RootChangedDuringRows_DoesNotFollowLinkForPublicationOrCleanup()
    {
        // Arrange
        var logs = new SafeLogger();
        using var fixture = new Fixture(logger: logs);
        using var outside = new Fixture();
        var movedRoot = fixture.Root + "-moved";
        using var lease = fixture.Gate.Acquire();
        // Alter after the file is closed; a custom row enumerator cannot move an open Windows directory.
        // The stream wrapper changes the root at disposal, before the exporter's final safety checks.
        var relocating = new RelocatingOperations(() =>
        {
            Directory.Move(fixture.Root, movedRoot);
            CreateJunction(fixture.Root, outside.Root);
        });
        var writer = new CsvExporter(Options.Create(new ExportOptions { OutputRoot = fixture.Root }), fixture.Gate, relocating, logs);
        try
        {
            // Act
            var error = await Assert.ThrowsAsync<ExportException>(() => writer.ExportAsync(ExportFile.Forces, [], lease, default));
            // Assert
            Assert.Equal("unsafe_export_path", error.Code);
            Assert.Empty(Directory.GetFileSystemEntries(outside.Root));
            Assert.Single(Directory.GetFiles(movedRoot)); // Unsafe cleanup is deliberately refused.
            Assert.Contains("export_cleanup_failed", Assert.Single(logs.Messages));
            Assert.DoesNotContain(fixture.Root, logs.Messages[0]);
        }
        finally
        {
            Directory.Delete(fixture.Root);
            Directory.Move(movedRoot, fixture.Root);
        }
    }

    [Fact]
    public async Task Export_CleanupFails_PreservesPrimaryFailureAndLogsSafeCode()
    {
        // Arrange
        var operations = new FaultOperations { Failure = "cleanup" };
        var logs = new SafeLogger();
        using var fixture = new Fixture(operations, logs);
        using var lease = fixture.Gate.Acquire();
        await File.WriteAllTextAsync(fixture.Final, "old");
        // Act
        var error = await Assert.ThrowsAsync<ExportException>(() => fixture.Writer.ExportAsync(
            ExportFile.Forces, [[CsvCell.Text("sensitive row with missing cell")]], lease, default));
        // Assert
        Assert.Equal("invalid_csv_row", error.Code);
        Assert.Equal("old", await File.ReadAllTextAsync(fixture.Final));
        Assert.Contains("export_cleanup_failed", Assert.Single(logs.Messages));
        Assert.DoesNotContain("sensitive", logs.Messages[0]);
        Assert.DoesNotContain(fixture.Root, logs.Messages[0]);
    }

    [Fact]
    public async Task Export_CancelledAfterLastRow_StopsBeforePublication()
    {
        // Arrange
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        using var lease = fixture.Gate.Acquire();
        await File.WriteAllTextAsync(fixture.Final, "old");
        IEnumerable<IReadOnlyList<CsvCell>> Rows()
        {
            yield return [CsvCell.Text("new"), CsvCell.Text("row")];
            cancellation.Cancel();
        }
        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Writer.ExportAsync(ExportFile.Forces, Rows(), lease, cancellation.Token));
        Assert.Equal("old", await File.ReadAllTextAsync(fixture.Final));
        Assert.Equal([fixture.Final], Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task Export_CancelledAtCommitPoint_ReportsCompletedPublication()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var operations = new FaultOperations { BeforePublish = () => cancellation.Cancel() };
        using var fixture = new Fixture(operations);
        using var lease = fixture.Gate.Acquire();
        // Act
        var count = await fixture.Writer.ExportAsync(ExportFile.Forces, [], lease, cancellation.Token);
        // Assert
        Assert.Equal(0, count);
        Assert.Equal("id,name\r\n", await File.ReadAllTextAsync(fixture.Final));
        Assert.Single(Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task Export_OptionsMutated_KeepsValidatedRootSnapshotAndCreatesMissingDirectory()
    {
        // Arrange
        using var fixture = new Fixture();
        using var outside = new Fixture();
        Directory.Delete(fixture.Root);
        var options = new ExportOptions { OutputRoot = fixture.Root };
        var writer = new CsvExporter(Options.Create(options), fixture.Gate, new AtomicFileOperations(), NullLogger<CsvExporter>.Instance);
        options.OutputRoot = outside.Root;
        using var lease = fixture.Gate.Acquire();
        // Act
        await writer.ExportAsync(ExportFile.Forces, [], lease, default);
        // Assert
        Assert.Equal("id,name\r\n", await File.ReadAllTextAsync(fixture.Final));
        Assert.Empty(Directory.GetFiles(outside.Root));
    }

    private static void CreateJunction(string link, string target)
    {
        // Windows local fixed disks are the supported runtime; junctions need no symlink privilege.
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    private sealed class SafeLogger : ILogger<CsvExporter>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        { Assert.Null(error); Messages.Add(formatter(state, error)); }
    }

    private sealed class RelocatingOperations(Action relocate) : IAtomicFileOperations
    {
        private readonly AtomicFileOperations real = new();
        public Stream CreateTemporary(string path) => new RelocatingStream(real.CreateTemporary(path), relocate);
        public void Publish(string temporary, string destination) => real.Publish(temporary, destination);
        public void DeleteTemporary(string path) => real.DeleteTemporary(path);
    }

    private sealed class RelocatingStream(Stream inner, Action relocate) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => inner.WriteAsync(buffer, token);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); relocate(); }
    }
}
