using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Test_Proj.Export;
using Test_Proj.Options;
using Test_Proj.Validation;

namespace PoliceDataIngestion.Api.Tests.Export;

public sealed class ExportTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("a\rb", "\"a\rb\"")]
    [InlineData("a\nb", "\"a\nb\"")]
    [InlineData("a\r\nb", "\"a\r\nb\"")]
    [InlineData("警察é😀", "警察é😀")]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@x", "'@x")]
    [InlineData("  =x", "'  =x")]
    [InlineData("\u0001\u0000 +x", "'\u0001\u0000 +x")]
    [InlineData("\u2003@x", "'\u2003@x")]
    [InlineData("\tplain", "'\tplain")]
    [InlineData("\rplain", "\"'\rplain\"")]
    [InlineData("\nplain", "\"'\nplain\"")]
    [InlineData(" \t\r-2,\"x\"", "\"' \t\r-2,\"\"x\"\"\"")]
    [InlineData("  plain", "  plain")]
    [InlineData("  ", "  ")]
    public async Task Export_TextCells_EncodesExactProtectedUtf8Records(string? input, string expected)
    {
        // Arrange
        using var fixture = new Fixture();
        using var lease = fixture.Gate.Acquire();
        // Act
        var count = await fixture.Writer.ExportAsync(ExportFile.Forces,
            [[CsvCell.Text(input), default]], lease, default);
        // Assert
        Assert.Equal(1, count);
        Assert.Equal(new UTF8Encoding(false).GetBytes("id,name\r\n" + expected + ",\r\n"),
            await File.ReadAllBytesAsync(fixture.Final));
        Assert.Equal([fixture.Final], Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task Export_TypedValues_FormatsInvariantNumbersBooleansAndOffsets()
    {
        // Arrange
        using var fixture = new Fixture();
        using var lease = fixture.Gate.Acquire();
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var timestamp = new DateTimeOffset(2026, 9, 1, 12, 34, 56, TimeSpan.FromHours(2));
            // Act
            await fixture.Writer.ExportAsync(ExportFile.Forces,
                [[CsvCell.Number(-1.5491), CsvCell.Number(null)],
                 [CsvCell.Integer(long.MinValue), CsvCell.Integer(null)],
                 [CsvCell.Boolean(true), CsvCell.Boolean(false)],
                 [CsvCell.Timestamp(timestamp), CsvCell.Timestamp(null)],
                 [CsvCell.Boolean(null), CsvCell.Number(-0d)]], lease, default);
            // Assert
            Assert.Equal("id,name\r\n-1.5491,\r\n-9223372036854775808,\r\ntrue,false\r\n2026-09-01T12:34:56.0000000+02:00,\r\n,-0\r\n",
                await File.ReadAllTextAsync(fixture.Final));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Number_NonfiniteValue_Rejects(double value)
    {
        // Arrange / Act
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => CsvCell.Number(value));
        // Assert
        Assert.Equal("value", error.ParamName);
    }

    [Fact]
    public async Task Export_CodeOwnedSchemas_WritesExactHeaderOnlyFiles()
    {
        // Arrange
        using var fixture = new Fixture();
        using var lease = fixture.Gate.Acquire();
        var query = LocationMonth.Create(0, 0, "0001-01");
        var files = new[] { ExportFile.Forces, ExportFile.Crimes(query), ExportFile.StopSearches(query) };
        var expected = new[] {
            "id,name\r\n",
            "id,persistent_id,category,month,latitude,longitude,street_id,street_name,location_type,context,outcome_category,outcome_date\r\n",
            "type,datetime,age_range,gender,self_defined_ethnicity,officer_defined_ethnicity,legislation,object_of_search,outcome,involved_person,operation,operation_name,latitude,longitude,street_id,street_name\r\n" };
        // Act / Assert
        for (var i = 0; i < files.Length; i++)
        {
            Assert.Equal(0, await fixture.Writer.ExportAsync(files[i], [], lease, default));
            Assert.Equal(expected[i], await File.ReadAllTextAsync(Path.Combine(fixture.Root, files[i].Filename)));
        }
        Assert.Equal(["Crimes_0001-01.csv", "Forces.csv", "StopSearches_0001-01.csv"],
            Directory.GetFiles(fixture.Root).Select(path => Path.GetFileName(path)).Order().ToArray());
    }

    [Theory]
    [InlineData("../escape.csv")]
    [InlineData("..\\escape.csv")]
    [InlineData("C:\\escape.csv")]
    [InlineData("/escape.csv")]
    [InlineData("\\\\server\\escape.csv")]
    [InlineData("Forces.csv:stream")]
    [InlineData("../output-sibling/escape.csv")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("")]
    public void Resolve_PathInput_RejectsEscape(string filename)
    {
        // Arrange
        using var fixture = new Fixture();
        // Act
        var error = Assert.Throws<ExportException>(() => ExportPath.Resolve(fixture.Root, filename));
        // Assert
        Assert.Equal("unsafe_export_path", error.Code);
        Assert.Empty(Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task Export_RepeatedAndEmpty_AtomicallyReplacesExistingFile()
    {
        // Arrange
        using var fixture = new Fixture();
        using var lease = fixture.Gate.Acquire();
        await File.WriteAllTextAsync(fixture.Final, "previous complete export");
        // Act
        await fixture.Writer.ExportAsync(ExportFile.Forces, [[CsvCell.Text("new"), CsvCell.Text("name")]], lease, default);
        // Assert
        Assert.Equal("id,name\r\nnew,name\r\n", await File.ReadAllTextAsync(fixture.Final));
        Assert.Equal(0, await fixture.Writer.ExportAsync(ExportFile.Forces, [], lease, default));
        Assert.Equal("id,name\r\n", await File.ReadAllTextAsync(fixture.Final));
        Assert.Single(Directory.GetFiles(fixture.Root));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("publish")]
    public async Task Export_FileSystemFailure_PreservesPreviousAndCleansTemporary(string failure)
    {
        // Arrange
        var operations = new FaultOperations { Failure = failure };
        using var fixture = new Fixture(operations);
        await File.WriteAllTextAsync(fixture.Final, "old complete");
        // Act
        using (var lease = fixture.Gate.Acquire())
        {
            var error = await Assert.ThrowsAsync<ExportException>(() => fixture.Writer.ExportAsync(
                ExportFile.Forces, [[CsvCell.Text("new"), CsvCell.Text("row")]], lease, default));
            // Assert
            Assert.Equal("export_failed", error.Code);
            Assert.DoesNotContain("sensitive", error.ToString());
            Assert.DoesNotContain(fixture.Root, error.ToString());
        }
        Assert.Equal("old complete", await File.ReadAllTextAsync(fixture.Final));
        Assert.Equal([fixture.Final], Directory.GetFiles(fixture.Root));
        using var next = fixture.Gate.Acquire();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_CancellationDuringRows_PreservesFinalAndReleasesLease(bool existing)
    {
        // Arrange
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        if (existing) await File.WriteAllTextAsync(fixture.Final, "old");
        IEnumerable<IReadOnlyList<CsvCell>> Rows()
        {
            yield return [CsvCell.Text("first"), CsvCell.Text("row")];
            cancellation.Cancel();
            yield return [CsvCell.Text("second"), CsvCell.Text("row")];
        }
        // Act / Assert
        using (var lease = fixture.Gate.Acquire())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Writer.ExportAsync(
                ExportFile.Forces, Rows(), lease, cancellation.Token));
        Assert.Equal(existing ? [fixture.Final] : [], Directory.GetFiles(fixture.Root));
        if (existing) Assert.Equal("old", await File.ReadAllTextAsync(fixture.Final));
        using var next = fixture.Gate.Acquire();
    }

    [Fact]
    public async Task Export_PreCancelled_DoesNotCreateDirectory()
    {
        // Arrange
        using var fixture = new Fixture();
        Directory.Delete(fixture.Root);
        using var lease = fixture.Gate.Acquire();
        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Writer.ExportAsync(
            ExportFile.Forces, [], lease, new CancellationToken(true)));
        // Assert
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public async Task Export_CancelledDuringFileWrite_PropagatesTokenAndCleansTemporary()
    {
        // Arrange
        var operations = new FaultOperations { Failure = "cancel-write" };
        using var fixture = new Fixture(operations);
        using var cancellation = new CancellationTokenSource();
        using var lease = fixture.Gate.Acquire();
        await File.WriteAllTextAsync(fixture.Final, "old");
        // Act
        var task = fixture.Writer.ExportAsync(ExportFile.Forces, [], lease, cancellation.Token);
        await operations.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        // Assert
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal("old", await File.ReadAllTextAsync(fixture.Final));
        Assert.Equal([fixture.Final], Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task Export_RowEnumerationFails_PreservesOldFileAndRemovesPartialOutput()
    {
        // Arrange
        using var fixture = new Fixture();
        using var lease = fixture.Gate.Acquire();
        await File.WriteAllTextAsync(fixture.Final, "old");
        IEnumerable<IReadOnlyList<CsvCell>> Rows()
        {
            yield return [CsvCell.Text("first"), CsvCell.Text("row")];
            throw new InvalidOperationException("sensitive payload");
        }
        // Act
        var error = await Assert.ThrowsAsync<ExportException>(() => fixture.Writer.ExportAsync(ExportFile.Forces, Rows(), lease, default));
        // Assert
        Assert.Equal("export_failed", error.Code);
        Assert.DoesNotContain("sensitive", error.ToString());
        Assert.Equal("old", await File.ReadAllTextAsync(fixture.Final));
        Assert.Equal([fixture.Final], Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task Export_InvalidRowOrEncoding_DoesNotPublishPartialData()
    {
        // Arrange
        using var fixture = new Fixture();
        using var lease = fixture.Gate.Acquire();
        await File.WriteAllTextAsync(fixture.Final, "old");
        // Act / Assert
        var rowError = await Assert.ThrowsAsync<ExportException>(() => fixture.Writer.ExportAsync(
            ExportFile.Forces, [[CsvCell.Text("missing second column")]], lease, default));
        Assert.Equal("invalid_csv_row", rowError.Code);
        var encodingError = await Assert.ThrowsAsync<ExportException>(() => fixture.Writer.ExportAsync(
            ExportFile.Forces, [[CsvCell.Text("\ud800"), default]], lease, default));
        Assert.Equal("export_failed", encodingError.Code);
        Assert.Equal("old", await File.ReadAllTextAsync(fixture.Final));
        Assert.Equal([fixture.Final], Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task Export_ActualLockedDestination_PreservesOldFileAndCleansTemporary()
    {
        // Arrange
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.Final, "old");
        using var lease = fixture.Gate.Acquire();
        using (var locked = new FileStream(fixture.Final, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Act
            var error = await Assert.ThrowsAsync<ExportException>(() => fixture.Writer.ExportAsync(
                ExportFile.Forces, [], lease, default));
            // Assert
            Assert.Equal("export_failed", error.Code);
        }
        Assert.Equal("old", await File.ReadAllTextAsync(fixture.Final));
        Assert.Equal([fixture.Final], Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task Export_ConcurrentOperations_RejectsImmediatelyAndDoesNotInterleave()
    {
        // Arrange
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IEnumerable<IReadOnlyList<CsvCell>> Rows()
        {
            entered.SetResult();
            resume.Task.GetAwaiter().GetResult();
            yield return [CsvCell.Text("first"), CsvCell.Text("only")];
        }
        using var lease = fixture.Gate.Acquire();
        var first = Task.Run(() => fixture.Writer.ExportAsync(ExportFile.Forces, Rows(), lease, default));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            // Act / Assert
            var busy = Assert.Throws<ExportException>(() => fixture.Gate.Acquire());
            Assert.Equal(409, busy.StatusCode);
            var reused = await Assert.ThrowsAsync<ExportException>(() => fixture.Writer.ExportAsync(
                ExportFile.Forces, [], lease, default));
            Assert.Equal("invalid_operation_lease", reused.Code);
            lease.Dispose(); // Even premature disposal cannot admit work during the write.
            Assert.Throws<ExportException>(() => fixture.Gate.Acquire());
        }
        finally { resume.TrySetResult(); }
        Assert.Equal(1, await first);
        Assert.Equal("id,name\r\nfirst,only\r\n", await File.ReadAllTextAsync(fixture.Final));
        using var next = fixture.Gate.Acquire();
    }

    [Fact]
    public async Task Export_ForeignOrDisposedLease_RejectsBeforeFileWork()
    {
        // Arrange
        using var fixture = new Fixture();
        using var foreign = new OperationLease().Acquire();
        var disposed = fixture.Gate.Acquire();
        disposed.Dispose();
        disposed.Dispose();
        // Act / Assert
        foreach (var lease in new[] { foreign, disposed })
            Assert.Equal("invalid_operation_lease", (await Assert.ThrowsAsync<ExportException>(() =>
                fixture.Writer.ExportAsync(ExportFile.Forces, [], lease, default))).Code);
        Assert.Empty(Directory.GetFiles(fixture.Root));
        using var next = fixture.Gate.Acquire();
    }

    [Fact]
    public void Registration_DifferentScopes_SharesSingleAdmissionAndExporter()
    {
        // Arrange
        using var fixture = new Fixture();
        var services = new ServiceCollection().AddLogging().AddCsvExport();
        services.AddSingleton<IOptions<ExportOptions>>(Options.Create(new ExportOptions { OutputRoot = fixture.Root }));
        using var provider = services.BuildServiceProvider();
        using var one = provider.CreateScope();
        using var two = provider.CreateScope();
        // Act
        var first = one.ServiceProvider.GetRequiredService<OperationLease>();
        var second = two.ServiceProvider.GetRequiredService<OperationLease>();
        // Assert
        Assert.Same(first, second);
        Assert.Same(one.ServiceProvider.GetRequiredService<ICsvExporter>(), two.ServiceProvider.GetRequiredService<ICsvExporter>());
        using var lease = first.Acquire();
        Assert.Equal("operation_busy", Assert.Throws<ExportException>(() => second.Acquire()).Code);
    }

    internal sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "police-export-tests-" + Guid.NewGuid().ToString("N"));
        public string Final => Path.Combine(Root, "Forces.csv");
        public OperationLease Gate { get; } = new();
        public CsvExporter Writer { get; }
        public Fixture(IAtomicFileOperations? files = null, ILogger<CsvExporter>? logger = null)
        {
            Directory.CreateDirectory(Root);
            Writer = new(Options.Create(new ExportOptions { OutputRoot = Root }), Gate,
                files ?? new AtomicFileOperations(), logger ?? NullLogger<CsvExporter>.Instance);
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    internal sealed class FaultOperations : IAtomicFileOperations
    {
        private readonly AtomicFileOperations real = new();
        public string? Failure { get; set; }
        public List<string> TemporaryPaths { get; } = [];
        public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? BeforePublish { get; set; }
        public Stream CreateTemporary(string path)
        {
            TemporaryPaths.Add(path);
            if (Failure == "create") throw new UnauthorizedAccessException("sensitive path denied");
            var stream = real.CreateTemporary(path);
            return Failure is "write" or "flush" or "cancel-write" ? new FaultStream(stream, Failure, WriteEntered) : stream;
        }
        public void Publish(string temporary, string destination)
        {
            BeforePublish?.Invoke();
            if (Failure == "publish") throw new IOException("sensitive disk failure");
            real.Publish(temporary, destination);
        }
        public void DeleteTemporary(string path)
        {
            if (Failure == "cleanup") throw new IOException("sensitive cleanup failure");
            real.DeleteTemporary(path);
        }
    }

    private sealed class FaultStream(Stream inner, string failure, TaskCompletionSource entered) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { if (failure == "flush") throw new IOException("sensitive flush"); inner.Flush(); }
        public override Task FlushAsync(CancellationToken token) => failure == "flush"
            ? Task.FromException(new IOException("sensitive flush")) : inner.FlushAsync(token);
        public override void Write(byte[] buffer, int offset, int count)
        { if (failure == "write") throw new IOException("sensitive disk full"); inner.Write(buffer, offset, count); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (failure == "write") throw new IOException("sensitive disk full");
            if (failure == "cancel-write")
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            await inner.WriteAsync(buffer, token);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
