using Xunit;

namespace LogicAppQuery.Tests;

public class SearchCancellationTests
{
    [Fact]
    public async Task WatchForEscapeAsync_Escape_CancelsPendingWork()
    {
        using var cancellation = new CancellationTokenSource();
        var pendingWork = Task.Delay(Timeout.Infinite, cancellation.Token);

        await SearchCancellation.WatchForEscapeAsync(cancellation, () => ConsoleKey.Escape)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(cancellation.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingWork);
    }

    [Fact]
    public async Task WatchForEscapeAsync_OtherKeysAndNoInput_ContinueUntilEscape()
    {
        using var cancellation = new CancellationTokenSource();
        var keys = new Queue<ConsoleKey?>([null, ConsoleKey.Enter, ConsoleKey.A, ConsoleKey.Escape]);

        await SearchCancellation.WatchForEscapeAsync(cancellation, () =>
        {
            Assert.False(cancellation.IsCancellationRequested);
            return keys.Dequeue();
        }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(keys);
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task WatchForEscapeAsync_ExternalCancellation_StopsPolling()
    {
        using var parent = new CancellationTokenSource();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(parent.Token);
        var reads = 0;
        var watcher = SearchCancellation.WatchForEscapeAsync(cancellation, () =>
        {
            reads++;
            return null;
        });

        parent.Cancel();
        await watcher.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, reads);
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task WatchForEscapeAsync_AlreadyCancelled_DoesNotReadInput()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await SearchCancellation.WatchForEscapeAsync(cancellation,
            () => throw new InvalidOperationException("Input should not be read."));
    }
}
