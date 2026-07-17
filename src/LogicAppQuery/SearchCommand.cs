using Spectre.Console;
using System.Text.Json;

namespace LogicAppQuery;

internal sealed class SearchCommand(IArmClient armClient, string? cacheDirectory = null, IAnsiConsole? ansiConsole = null)
{
    const int SnippetRadius = 100;
    const int MaxSnippetLength = 300;

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

        int runCount = 0, matchCount = 0, fetchFailCount = 0, cacheHits = 0;

        await _console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Starting...", async ctx =>
            {
                await foreach (var run in armClient.ListRunsAsync(
                    subscriptionId, resourceGroup, appName, workflowName, start, end, ct))
                {
                    runCount++;
                    ctx.Status(
                        $"{run.Properties.StartTime.UtcDateTime:yyyy-MM-dd}  |  " +
                        $"Searched {runCount} run(s), {matchCount} match(es)" +
                        (cacheHits > 0 ? $" ({cacheHits} from cache)" : "") +
                        (fetchFailCount > 0 ? $", {fetchFailCount} unreadable" : "") +
                        "...");

                    var isTerminal = RunCache.IsTerminal(run.Properties.Status);
                    string? content;

                    var cached = isTerminal ? await cache.TryGetAsync(run.Name) : null;

                    if (cached is not null)
                    {
                        content = cached.Content;
                        cacheHits++;
                    }
                    else
                    {
                        content = await BuildRunContentAsync(
                            run, subscriptionId, resourceGroup, appName, workflowName, ct);

                        if (content is null)
                        {
                            fetchFailCount++;
                            continue;
                        }

                        if (isTerminal)
                            await cache.SetAsync(run.Name, new CachedRun(
                                run.Properties.Status, run.Properties.StartTime, content));
                    }

                    if (!content.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                        continue;

                    matchCount++;
                    var snippet = BuildSnippet(content, searchTerm);
                    var statusColor = run.Properties.Status switch
                    {
                        "Succeeded"              => "green",
                        "Failed"                 => "red",
                        "Running"                => "blue",
                        "Cancelled" or "Skipped" => "grey",
                        _                        => "white"
                    };

                    _console.MarkupLine(
                        $"[bold green]MATCH[/]  " +
                        $"[grey]{run.Properties.StartTime.UtcDateTime:yyyy-MM-dd HH:mm:ss}[/]  " +
                        $"[{statusColor}]{Markup.Escape(run.Properties.Status)}[/]  " +
                        $"[dim]{Markup.Escape(run.Name)}[/]");
                    _console.MarkupLine($"  [dim italic]{Markup.Escape(snippet)}[/]");
                    _console.WriteLine();
                }
            });

        _console.Write(new Rule());
        _console.MarkupLine(
            matchCount > 0
                ? $"Searched [bold]{runCount}[/] run(s). Found [bold green]{matchCount}[/] match(es)."
                : $"Searched [bold]{runCount}[/] run(s). [yellow]No matches found.[/]");
        if (cacheHits > 0)
            _console.MarkupLine($"[grey]{cacheHits} run(s) loaded from cache.[/]");
        if (fetchFailCount > 0)
            _console.MarkupLine($"[yellow]Warning:[/] Could not read content for {fetchFailCount} run(s) — they were skipped.");
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
                AnsiConsole.MarkupLine($"[yellow]Warning:[/] Failed to fetch content link ({Markup.Escape(ex.Message)}). Falling back to inlined content.");
            }
        }

        if (inlined is { ValueKind: not JsonValueKind.Undefined } el)
            return el.GetRawText();

        return null;
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
        var snippet = prefix + raw + suffix;
        return snippet.Length > MaxSnippetLength ? snippet[..MaxSnippetLength] + "..." : snippet;
    }
}

