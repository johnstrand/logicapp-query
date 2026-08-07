using Spectre.Console;
using System.Text.Json;

namespace LogicAppQuery;

internal sealed class SearchCommand(IArmClient armClient, string? cacheDirectory = null, IAnsiConsole? ansiConsole = null)
{
    const int SnippetRadius = 100;
    const int MaxSnippetLength = 300;
    private readonly object _consoleLock = new();

    private readonly IAnsiConsole _console = ansiConsole ?? AnsiConsole.Console;

    public async Task ExecuteAsync(
        string subscriptionId,
        string appName,
        string workflowName,
        string searchTerm,
        DateTimeOffset? start,
        DateTimeOffset? end,
        CancellationToken ct)
    {
        _console.MarkupLine($"[bold]App:[/]      [cyan]{Markup.Escape(appName)}[/]");
        _console.MarkupLine($"[bold]Workflow:[/] [cyan]{Markup.Escape(workflowName)}[/]");
        _console.MarkupLine($"[bold]Search:[/]   [yellow]{Markup.Escape(searchTerm)}[/]");
        if (start.HasValue) _console.MarkupLine($"[bold]From:[/]     {start.Value.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC");
        if (end.HasValue)   _console.MarkupLine($"[bold]To:[/]       {end.Value.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC");
        _console.WriteLine();

        _console.Markup("[grey]Discovering resource group...[/] ");
        string resourceGroup;
        try
        {
            resourceGroup = await armClient.DiscoverResourceGroupAsync(subscriptionId, appName, ct);
        }
        catch (Exception ex)
        {
            _console.MarkupLine("[red]failed[/]");
            _console.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return;
        }
        _console.MarkupLine($"[green]{Markup.Escape(resourceGroup)}[/]");
        _console.WriteLine();

        await using var cache = await RunCache.LoadAsync(appName, workflowName, cacheDirectory);

        var state = new SearchState
        {
            Cache = cache,
            SubscriptionId = subscriptionId,
            ResourceGroup = resourceGroup,
            AppName = appName,
            WorkflowName = workflowName,
            SearchTerm = searchTerm
        };

        await _console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Starting...", async ctx =>
            {
                var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2, CancellationToken = ct };
                await Parallel.ForEachAsync(armClient.ListRunsAsync(
                    subscriptionId, resourceGroup, appName, workflowName, start, end, ct), options, async (run, token) =>
                {
                    await ProcessRunAsync(run, state, ctx, token);
                });
            });

        _console.Write(new Rule());
        _console.MarkupLine(
            state.MatchCount > 0
                ? $"Searched [bold]{state.RunCount}[/] run(s). Found [bold green]{state.MatchCount}[/] match(es)."
                : $"Searched [bold]{state.RunCount}[/] run(s). [yellow]No matches found.[/]");
        if (state.CacheHits > 0)
            _console.MarkupLine($"[grey]{state.CacheHits} run(s) loaded from cache.[/]");
        if (state.FetchFailCount > 0)
            _console.MarkupLine($"[yellow]Warning:[/] Could not read content for {state.FetchFailCount} run(s) — they were skipped.");
    }

    private async Task ProcessRunAsync(WorkflowRun run, SearchState state, StatusContext ctx, CancellationToken token)
    {
        Interlocked.Increment(ref state.RunCount);
        ctx.Status(
            $"{run.Properties.StartTime.UtcDateTime:yyyy-MM-dd}  |  " +
            $"Searched {state.RunCount} run(s), {state.MatchCount} match(es)" +
            (state.CacheHits > 0 ? $" ({state.CacheHits} from cache)" : "") +
            (state.FetchFailCount > 0 ? $", {state.FetchFailCount} unreadable" : "") +
            "...");

        var isTerminal = RunCache.IsTerminal(run.Properties.Status);
        string? content;

        var cached = isTerminal ? await state.Cache.TryGetAsync(run.Name) : null;

        if (cached is not null)
        {
            content = cached.Content;
            Interlocked.Increment(ref state.CacheHits);
        }
        else
        {
            content = await BuildRunContentAsync(
                run, state.SubscriptionId, state.ResourceGroup, state.AppName, state.WorkflowName, token);

            if (content is null)
            {
                Interlocked.Increment(ref state.FetchFailCount);
                return; // `continue` in `foreach` becomes `return` in `ForEachAsync` delegate
            }

            if (isTerminal)
                await state.Cache.SetAsync(run.Name, new CachedRun(
                    run.Properties.Status, run.Properties.StartTime, content));
        }

        if (!content.AsSpan().Contains(state.SearchTerm.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return;

        Interlocked.Increment(ref state.MatchCount);
        var snippet = BuildSnippet(content, state.SearchTerm);
        var statusColor = run.Properties.Status switch
        {
            "Succeeded"              => "green",
            "Failed"                 => "red",
            "Running"                => "blue",
            "Cancelled" or "Skipped" => "grey",
            _                        => "white"
        };

        lock (_consoleLock) // prevent concurrent console writes from overlapping
        {
            _console.MarkupLine(
                $"[bold green]MATCH[/]  " +
                $"[grey]{run.Properties.StartTime.UtcDateTime:yyyy-MM-dd HH:mm:ss}[/]  " +
                $"[{statusColor}]{Markup.Escape(run.Properties.Status)}[/]  " +
                $"[dim]{Markup.Escape(run.Name)}[/]");
            _console.MarkupLine($"  [dim italic]{Markup.Escape(snippet)}[/]");
            _console.WriteLine();
        }
    }

    internal async Task<string?> BuildRunContentAsync(
        WorkflowRun run,
        string subscriptionId,
        string resourceGroup,
        string appName,
        string workflowName,
        CancellationToken ct)
    {
        var tasks = new List<Task<string?>>();
        using var semaphore = new SemaphoreSlim(10, 10);

        async Task<string?> FetchWithSemaphoreAsync(ContentLink? link, JsonElement? inlined, CancellationToken token)
        {
            await semaphore.WaitAsync(token);
            try
            {
                return await FetchContentPartAsync(link, inlined, token);
            }
            finally
            {
                semaphore.Release();
            }
        }

        // Trigger outputs (the actual inbound payload)
        tasks.Add(FetchWithSemaphoreAsync(
            run.Properties.Trigger?.OutputsLink,
            run.Properties.Trigger?.Outputs,
            ct));

        // All action inputs and outputs
        await foreach (var action in armClient.ListActionsAsync(
            subscriptionId, resourceGroup, appName, workflowName, run.Name, ct))
        {
            tasks.Add(FetchWithSemaphoreAsync(
                action.Properties.InputsLink,
                action.Properties.Inputs,
                ct));

            tasks.Add(FetchWithSemaphoreAsync(
                action.Properties.OutputsLink,
                action.Properties.Outputs,
                ct));
        }

        var results = await Task.WhenAll(tasks);
        var parts = new List<string>();
        foreach (var res in results)
        {
            if (res is not null) parts.Add(res);
        }

        return parts.Count > 0 ? string.Join("\n", parts) : null;
    }

    async Task<string?> FetchContentPartAsync(
        ContentLink? link,
        JsonElement? inlined,
        CancellationToken ct)
    {
        if (link is not null)
        {
            try
            {
                var fetched = await armClient.FetchContentAsync(link, ct);
                if (fetched is not null) { return fetched; }
            }
            catch (Exception ex)
            {
                lock (_consoleLock)
                {
                    _console.MarkupLine($"[yellow]Warning:[/] Failed to fetch content link ({Markup.Escape(ex.Message)}). Falling back to inlined content.");
                }
            }
        }

        if (inlined is { ValueKind: not JsonValueKind.Undefined } el)
            return el.GetRawText();

        return null;
    }

    private sealed class SearchState
    {
        public int RunCount;
        public int MatchCount;
        public int FetchFailCount;
        public int CacheHits;

        public required RunCache Cache { get; init; }
        public required string SubscriptionId { get; init; }
        public required string ResourceGroup { get; init; }
        public required string AppName { get; init; }
        public required string WorkflowName { get; init; }
        public required string SearchTerm { get; init; }
    }

    internal static string BuildSnippet(string content, string searchTerm)
    {
        var idx = content.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return string.Empty;

        var start  = Math.Max(0, idx - SnippetRadius);
        var end    = Math.Min(content.Length, idx + searchTerm.Length + SnippetRadius);
        var raw    = content[start..end].ReplaceLineEndings(" ");
        var prefix = start > 0 ? "..." : "";
        var suffix = end < content.Length ? "..." : "";
        var snippet = $"{prefix}{raw}{suffix}";
        return snippet.Length > MaxSnippetLength ? $"{snippet.AsSpan(0, MaxSnippetLength)}..." : snippet;
    }
}

