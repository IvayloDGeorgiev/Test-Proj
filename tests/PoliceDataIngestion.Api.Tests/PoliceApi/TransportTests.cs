using Test_Proj.Errors;

namespace PoliceDataIngestion.Api.Tests.PoliceApi;

public sealed class TransportTests
{
    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, true)]
    [InlineData(HttpRequestError.ConnectionError, true)]
    [InlineData(HttpRequestError.ResponseEnded, true)]
    [InlineData(HttpRequestError.SecureConnectionError, false)]
    [InlineData(HttpRequestError.UserAuthenticationError, false)]
    [InlineData(HttpRequestError.HttpProtocolError, false)]
    [InlineData(HttpRequestError.ConfigurationLimitExceeded, false)]
    public async Task GetForces_ClassifiedTransportFailure_RetriesOnlyTransientErrors(HttpRequestError error, bool retry)
    {
        // Arrange
        using var fixture = new TransportFixture((attempt, _, _) => attempt == 1
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException(error, "synthetic sensitive diagnostic"))
            : Task.FromResult(TransportFixture.Response()));
        // Act
        var task = fixture.Client.GetForcesAsync();
        if (retry)
        {
            await fixture.AdvanceBackoffAsync();
            Assert.Empty(await task);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<PoliceApiException>(() => task);
            Assert.Equal(502, exception.StatusCode);
            Assert.Null(exception.InnerException);
        }
        // Assert
        Assert.Equal(retry ? 2 : 1, fixture.Handler.Calls);
        Assert.All(fixture.Logger.Messages, message => Assert.DoesNotContain("sensitive", message));
    }

    [Fact]
    public async Task GetForces_BodyReadFails_DiscardsPartialDataDisposesAndRetries()
    {
        // Arrange
        var stream = new FailingReadStream();
        using var fixture = new TransportFixture((attempt, _, _) => Task.FromResult(attempt == 1
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(stream) }
            : TransportFixture.Response()));
        // Act
        var task = fixture.Client.GetForcesAsync();
        await fixture.Clock.WaitForBackoffAsync();
        Assert.True(stream.Disposed);
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(500));
        var result = await task;
        // Assert
        Assert.Empty(result);
        Assert.Equal(2, fixture.Handler.Calls);
        Assert.All(fixture.Logger.Messages, message => Assert.DoesNotContain("secret", message));
    }

    [Fact]
    public async Task GetForces_DeclaredLengthOverLimit_RejectsBeforeReading()
    {
        // Arrange
        var stream = new BlockingStream();
        var content = new StreamContent(stream);
        content.Headers.ContentLength = 4;
        using var fixture = new TransportFixture((_, _, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = content }), new() { MaximumResponseBytes = 3 });
        // Act
        var exception = await Assert.ThrowsAsync<PoliceApiException>(() => fixture.Client.GetForcesAsync());
        // Assert
        Assert.Equal(PoliceApiFailure.ResponseLimit, exception.Failure);
        Assert.False(stream.Entered.Task.IsCompleted);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task GetForces_MisreportedShortLength_StillBoundsActualBytes()
    {
        // Arrange
        var content = new StreamContent(new NonSeekableStream("[  ]"u8.ToArray()));
        content.Headers.ContentLength = 2;
        using var fixture = new TransportFixture((_, _, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = content }), new() { MaximumResponseBytes = 3 });
        // Act
        var exception = await Assert.ThrowsAsync<PoliceApiException>(() => fixture.Client.GetForcesAsync());
        // Assert
        Assert.Equal(PoliceApiFailure.ResponseLimit, exception.Failure);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task GetForces_ValidArray_MapsFieldsIgnoresExtrasAndDisposesResponse()
    {
        // Arrange
        var content = new TrackingContent("[{\"id\":\"test\",\"name\":\"Test Police\",\"extra\":42}]");
        using var fixture = new TransportFixture((_, _, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content }));
        // Act
        var result = await fixture.Client.GetForcesAsync();
        // Assert
        var force = Assert.Single(result);
        Assert.Equal("test", force.Id);
        Assert.Equal("Test Police", force.Name);
        Assert.Equal("https://data.police.uk/api/forces", Assert.Single(fixture.Handler.Uris).AbsoluteUri);
        Assert.True(Assert.Single(fixture.Handler.Tokens).CanBeCanceled);
        Assert.True(content.Disposed);
        Assert.Contains(fixture.Logger.Messages, message => message.Contains("count 1") && message.Contains("success"));
    }

    [Theory]
    [InlineData("")] [InlineData("null")] [InlineData("{}")] [InlineData("[null]")]
    [InlineData("[1]")] [InlineData("[[]]")] [InlineData("[")] [InlineData("[] trailing")]
    [InlineData("[{\"id\":\"x\"}]")] [InlineData("[{\"name\":\"x\"}]")]
    [InlineData("[{\"id\":null,\"name\":\"x\"}]")]
    [InlineData("[{\"id\":12,\"name\":\"x\"}]")]
    [InlineData("[{\"id\":\"x\",\"name\":\"  \"}]")]
    public async Task GetForces_InvalidPayload_FailsWithoutRetry(string body)
    {
        // Arrange
        var content = new TrackingContent(body);
        using var fixture = new TransportFixture((_, _, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content }));
        // Act
        var exception = await Assert.ThrowsAsync<PoliceApiException>(() => fixture.Client.GetForcesAsync());
        // Assert
        Assert.Equal(PoliceApiFailure.InvalidPayload, exception.Failure);
        Assert.Equal(502, exception.StatusCode);
        Assert.Equal(1, fixture.Handler.Calls);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task GetForces_EmptyArray_ReturnsZeroRecords()
    {
        // Arrange
        using var fixture = new TransportFixture((_, _, _) => Task.FromResult(TransportFixture.Response()));
        // Act
        var result = await fixture.Client.GetForcesAsync();
        // Assert
        Assert.Empty(result);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData(201)] [InlineData(204)] [InlineData(301)] [InlineData(302)] [InlineData(307)]
    [InlineData(400)] [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(418)] [InlineData(501)]
    public async Task GetForces_PermanentOrUnexpectedStatus_DoesNotRetryOrLeak(int status)
    {
        // Arrange
        const string sensitive = "SYNTHETIC-SECRET C:\\private\\data lat=53.8";
        var content = new TrackingContent(sensitive);
        using var fixture = new TransportFixture((_, _, _) =>
        {
            var response = TransportFixture.Response(status);
            response.Content = content;
            response.Headers.Location = new Uri("https://untrusted.invalid/");
            return Task.FromResult(response);
        });
        // Act
        var exception = await Assert.ThrowsAsync<PoliceApiException>(() => fixture.Client.GetForcesAsync());
        // Assert
        Assert.Equal(PoliceApiFailure.UpstreamFailure, exception.Failure);
        Assert.Equal(1, fixture.Handler.Calls);
        Assert.True(content.Disposed);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(sensitive, exception.ToString());
        Assert.All(fixture.Logger.Messages, message => Assert.DoesNotContain(sensitive, message));
    }

    [Theory]
    [InlineData(408)] [InlineData(429)] [InlineData(500)] [InlineData(502)] [InlineData(503)] [InlineData(504)]
    public async Task GetForces_TransientStatus_RetriesAtMostThreeTimesWithBackoff(int status)
    {
        // Arrange
        var contents = new List<TrackingContent>();
        using var fixture = new TransportFixture((_, _, _) =>
        {
            var content = new TrackingContent("sensitive body");
            contents.Add(content);
            return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = content });
        }, jitter: 1);
        // Act
        var task = fixture.Client.GetForcesAsync();
        var first = await fixture.Clock.WaitForBackoffAsync();
        Assert.True(contents[0].Disposed);
        fixture.Clock.Advance(first);
        var second = await fixture.Clock.WaitForBackoffAsync();
        fixture.Clock.Advance(second);
        var exception = await Assert.ThrowsAsync<PoliceApiException>(() => task);
        // Assert
        Assert.Equal(TimeSpan.FromMilliseconds(750), first);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), second);
        Assert.Equal(3, fixture.Handler.Calls);
        Assert.Equal(status == 429 ? 503 : 502, exception.StatusCode);
        Assert.All(contents, content => Assert.True(content.Disposed));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GetForces_TransportFailure_RetriesThenSucceeds(bool ioFailure)
    {
        // Arrange
        using var fixture = new TransportFixture((attempt, _, _) => attempt == 1
            ? Task.FromException<HttpResponseMessage>(ioFailure ? new IOException("sensitive") : new HttpRequestException("sensitive"))
            : Task.FromResult(TransportFixture.Response()));
        // Act
        var task = fixture.Client.GetForcesAsync();
        await fixture.AdvanceBackoffAsync();
        var result = await task;
        // Assert
        Assert.Empty(result);
        Assert.Equal(2, fixture.Handler.Calls);
        Assert.All(fixture.Logger.Messages, message => Assert.DoesNotContain("sensitive", message));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task GetForces_ExhaustedNetwork_RespectsConfiguredAttempts(int attempts)
    {
        // Arrange
        using var fixture = new TransportFixture((_, _, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("DNS synthetic secret")),
            new() { MaximumAttempts = attempts });
        // Act
        var task = fixture.Client.GetForcesAsync();
        for (var i = 1; i < attempts; i++) await fixture.AdvanceBackoffAsync();
        var exception = await Assert.ThrowsAsync<PoliceApiException>(() => task);
        // Assert
        Assert.Equal(502, exception.StatusCode);
        Assert.Equal(attempts, fixture.Handler.Calls);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("delta", 3000)] [InlineData("date", 4000)]
    [InlineData("past", 500)] [InlineData("malformed", 500)] [InlineData("zero", 500)]
    public async Task GetForces_RetryAfter_HonorsMinimumOrFallsBack(string kind, int milliseconds)
    {
        // Arrange
        using var fixture = new TransportFixture((attempt, _, _) =>
        {
            var response = TransportFixture.Response(attempt == 1 ? 429 : 200);
            response.Headers.TryAddWithoutValidation("Retry-After", kind switch
            {
                "delta" => "3", "date" => "Wed, 16 Sep 2026 00:00:04 GMT",
                "past" => "Tue, 15 Sep 2026 00:00:00 GMT", "zero" => "0", _ => "invalid"
            });
            return Task.FromResult(response);
        });
        // Act
        var task = fixture.Client.GetForcesAsync();
        var delay = await fixture.Clock.WaitForBackoffAsync();
        Assert.Equal(1, fixture.Handler.Calls);
        fixture.Clock.Advance(delay - TimeSpan.FromTicks(1));
        Assert.Equal(1, fixture.Handler.Calls);
        fixture.Clock.Advance(TimeSpan.FromTicks(1));
        var result = await task;
        // Assert
        Assert.Empty(result);
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), delay);
        Assert.Equal(2, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData("120")] [InlineData("121")]
    [InlineData("Wed, 16 Sep 2026 00:03:00 GMT")]
    public async Task GetForces_RetryAfterCannotFit_StopsWith503(string header)
    {
        // Arrange
        using var fixture = new TransportFixture((_, _, _) =>
        {
            var response = TransportFixture.Response(503);
            response.Headers.TryAddWithoutValidation("Retry-After", header);
            return Task.FromResult(response);
        });
        // Act
        var exception = await Assert.ThrowsAsync<PoliceApiException>(() => fixture.Client.GetForcesAsync());
        // Assert
        Assert.Equal(PoliceApiFailure.RetryBudget, exception.Failure);
        Assert.Equal(503, exception.StatusCode);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData(2, "[]", false)] [InlineData(2, "[]", true)]
    [InlineData(3, "[ ]", false)] [InlineData(3, "[ ]", true)]
    public async Task GetForces_ExactByteLimit_AcceptsCompleteArray(int limit, string body, bool unknownLength)
    {
        // Arrange
        using var fixture = new TransportFixture((_, _, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = unknownLength ? new StreamContent(new NonSeekableStream(System.Text.Encoding.UTF8.GetBytes(body))) : new StringContent(body) }),
            new() { MaximumResponseBytes = limit });
        // Act
        var result = await fixture.Client.GetForcesAsync();
        // Assert
        Assert.Empty(result);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GetForces_TooManyBytes_FailsWithoutRetry(bool unknownLength)
    {
        // Arrange
        using var fixture = new TransportFixture((_, _, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = unknownLength ? new StreamContent(new NonSeekableStream("[  ]"u8.ToArray())) : new StringContent("[  ]") }),
            new() { MaximumResponseBytes = 3 });
        // Act
        var exception = await Assert.ThrowsAsync<PoliceApiException>(() => fixture.Client.GetForcesAsync());
        // Assert
        Assert.Equal(PoliceApiFailure.ResponseLimit, exception.Failure);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData(1, true)] [InlineData(2, false)]
    public async Task GetForces_RecordLimit_EnforcesBoundary(int limit, bool fails)
    {
        // Arrange
        using var fixture = new TransportFixture((_, _, _) => Task.FromResult(TransportFixture.Response(body:
            "[{\"id\":\"a\",\"name\":\"A\"},{\"id\":\"b\",\"name\":\"B\"}]")), new() { MaximumRecords = limit });
        // Act / Assert
        if (fails)
            Assert.Equal(PoliceApiFailure.ResponseLimit, (await Assert.ThrowsAsync<PoliceApiException>(() => fixture.Client.GetForcesAsync())).Failure);
        else Assert.Equal(2, (await fixture.Client.GetForcesAsync()).Count);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class FailingReadStream : MemoryStream
    {
        private bool read;
        public bool Disposed { get; private set; }
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (read) throw new IOException("synthetic secret in body error");
            read = true;
            buffer.Span[0] = (byte)'[';
            return ValueTask.FromResult(1);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
