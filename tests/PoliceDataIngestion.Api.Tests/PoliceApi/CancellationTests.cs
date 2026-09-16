using Test_Proj.Errors;

namespace PoliceDataIngestion.Api.Tests.PoliceApi;

public sealed class CancellationTests
{
    [Fact]
    public async Task GetForces_AllAttemptsTimeOut_Returns504AfterThreeAttempts()
    {
        // Arrange
        var entered = System.Threading.Channels.Channel.CreateUnbounded<int>();
        using var fixture = new TransportFixture(async (attempt, _, token) =>
        {
            entered.Writer.TryWrite(attempt);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TransportFixture.Response();
        });
        // Act
        var task = fixture.Client.GetForcesAsync();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Equal(attempt, await entered.Reader.ReadAsync());
            fixture.Clock.Advance(TimeSpan.FromSeconds(30));
            if (attempt < 3) await fixture.AdvanceBackoffAsync();
        }
        var exception = await Assert.ThrowsAsync<PoliceApiException>(() => task);
        // Assert
        Assert.Equal(504, exception.StatusCode);
        Assert.Equal("upstream_timeout", exception.Code);
        Assert.Equal(3, fixture.Handler.Calls);
        Assert.Equal(TimeSpan.FromMilliseconds(91500), fixture.Clock.GetElapsedTime(0));
    }

    [Fact]
    public async Task GetForces_CancelledBeforeCall_NeverSends()
    {
        // Arrange
        using var fixture = new TransportFixture((_, _, _) => Task.FromResult(TransportFixture.Response()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        // Act
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.GetForcesAsync(cancellation.Token));
        // Assert
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task GetForces_CancelledDuringSend_PropagatesWithoutRetry()
    {
        // Arrange
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new TransportFixture(async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TransportFixture.Response();
        });
        using var cancellation = new CancellationTokenSource();
        // Act
        var task = fixture.Client.GetForcesAsync(cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        // Assert
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(Assert.Single(fixture.Handler.Tokens).IsCancellationRequested);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task GetForces_BodyCancellationOrTimeout_DisposesStreamAndClassifies(bool callerCancels)
    {
        // Arrange
        var stream = new BlockingStream();
        using var fixture = new TransportFixture((_, _, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new StreamContent(stream) }), new() { MaximumAttempts = 1 });
        using var cancellation = new CancellationTokenSource();
        // Act
        var task = fixture.Client.GetForcesAsync(cancellation.Token);
        await stream.Entered.Task;
        if (callerCancels) cancellation.Cancel();
        else fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        // Assert
        if (callerCancels)
            Assert.Equal(cancellation.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task)).CancellationToken);
        else
            Assert.Equal(504, (await Assert.ThrowsAsync<PoliceApiException>(() => task)).StatusCode);
        Assert.True(stream.Disposed);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task GetForces_CancelledInBackoff_StopsBeforeNextSend()
    {
        // Arrange
        using var fixture = new TransportFixture((_, _, _) => Task.FromResult(TransportFixture.Response(503)));
        using var cancellation = new CancellationTokenSource();
        // Act
        var task = fixture.Client.GetForcesAsync(cancellation.Token);
        await fixture.Clock.WaitForBackoffAsync();
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        // Assert
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task GetForces_AttemptTimeout_RetriesWithinTotalBudget()
    {
        // Arrange
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new TransportFixture(async (attempt, _, token) =>
        {
            if (attempt == 1)
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return TransportFixture.Response();
        });
        // Act
        var task = fixture.Client.GetForcesAsync();
        await entered.Task;
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await fixture.AdvanceBackoffAsync();
        var result = await task;
        // Assert
        Assert.Empty(result);
        Assert.Equal(2, fixture.Handler.Calls);
        Assert.Equal(TimeSpan.FromMilliseconds(30500), fixture.Clock.GetElapsedTime(0));
    }

    [Fact]
    public async Task GetForces_TotalDeadlineDuringSend_DoesNotRetry()
    {
        // Arrange
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new TransportFixture(async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TransportFixture.Response();
        }, new() { AttemptTimeoutSeconds = 10, TotalOperationTimeoutSeconds = 10 });
        // Act
        var task = fixture.Client.GetForcesAsync();
        await entered.Task;
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        var exception = await Assert.ThrowsAsync<PoliceApiException>(() => task);
        // Assert
        Assert.Equal(PoliceApiFailure.Timeout, exception.Failure);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task GetForces_TotalDeadlineDuringBackoff_StopsWith504()
    {
        // Arrange
        using var fixture = new TransportFixture((_, _, _) => Task.FromResult(TransportFixture.Response(500)));
        // Act
        var task = fixture.Client.GetForcesAsync();
        await fixture.Clock.WaitForBackoffAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(120));
        var exception = await Assert.ThrowsAsync<PoliceApiException>(() => task);
        // Assert
        Assert.Equal(504, exception.StatusCode);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task GetForces_AttemptsConsumeBudget_RejectsUnfittableRetryDelay()
    {
        // Arrange
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new TransportFixture(async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TransportFixture.Response();
        }, new() { AttemptTimeoutSeconds = 30, TotalOperationTimeoutSeconds = 31 });
        // Act
        var task = fixture.Client.GetForcesAsync();
        await entered.Task;
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(30750));
        var exception = await Assert.ThrowsAsync<PoliceApiException>(() => task);
        // Assert
        Assert.Equal(503, exception.StatusCode);
        Assert.Equal(PoliceApiFailure.RetryBudget, exception.Failure);
        Assert.Equal(1, fixture.Handler.Calls);
    }
}
