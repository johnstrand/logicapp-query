using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace LogicAppQuery;

internal record CachedRun(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("startTime")] DateTimeOffset StartTime,
    [property: JsonPropertyName("content")] string Content
);

internal sealed class RunCache
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    static readonly HashSet<string> TerminalStates =
        ["Succeeded", "Failed", "Cancelled", "Skipped", "TimedOut", "Aborted"];

    readonly string _filePath;
    readonly Dictionary<string, CachedRun> _runs;
    bool _dirty;

    internal bool IsDirty => _dirty;

    internal RunCache(string filePath, Dictionary<string, CachedRun> runs)
    {
        _filePath = filePath;
        _runs = runs;
    }

    public static bool IsTerminal(string status) => TerminalStates.Contains(status);

    public static RunCache Load(string appName, string workflowName)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LogicAppQuery");
        Directory.CreateDirectory(dir);

        var fileName = Sanitize(appName) + "-" + Sanitize(workflowName) + ".cache.json";
        var filePath = Path.Combine(dir, fileName);

        if (File.Exists(filePath))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, CachedRun>>(json);
                if (dict is not null)
                    return new RunCache(filePath, dict);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning:[/] Could not load run cache. Starting fresh. ({Markup.Escape(ex.Message)})");
            }
        }

        return new RunCache(filePath, []);
    }

    public bool TryGet(string runName, [NotNullWhen(true)] out CachedRun? run)
        => _runs.TryGetValue(runName, out run);

    public void Set(string runName, CachedRun run)
    {
        _runs[runName] = run;
        _dirty = true;
    }

    public void Save()
    {
        if (!_dirty) return;
        var json = JsonSerializer.Serialize(_runs, JsonOptions);
        File.WriteAllText(_filePath, json);
        _dirty = false;
    }

    internal static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
        return sanitized.Replace('.', '_').Replace('/', '_').Replace('\\', '_');
    }
}
