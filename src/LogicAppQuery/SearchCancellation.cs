namespace LogicAppQuery;

internal static class SearchCancellation
{
    internal static async Task WatchForEscapeAsync(
        CancellationTokenSource cancellation,
        Func<ConsoleKey?> readKey)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (readKey() == ConsoleKey.Escape)
                {
                    cancellation.Cancel();
                    return;
                }

                await Task.Delay(50, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }
}
