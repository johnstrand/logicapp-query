using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Spectre.Console;

namespace LogicAppQuery;

internal record CachedRun(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("startTime")] DateTimeOffset StartTime,
    [property: JsonPropertyName("content")] string Content
);

internal sealed class RunCache : IAsyncDisposable
{
    static readonly HashSet<string> TerminalStates =
        new(StringComparer.OrdinalIgnoreCase) { "Succeeded", "Failed", "Cancelled", "Skipped", "TimedOut", "Aborted" };

    readonly SqliteConnection _connection;
    readonly string _appName;
    readonly string _workflowName;

    private RunCache(SqliteConnection connection, string appName, string workflowName)
    {
        _connection = connection;
        _appName = appName;
        _workflowName = workflowName;
    }

    public static bool IsTerminal(string status) => TerminalStates.Contains(status);

    public static async Task<RunCache> LoadAsync(string appName, string workflowName, string? directory = null)
    {
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LogicAppQuery");
        Directory.CreateDirectory(dir);

        var dbPath = Path.Combine(dir, "LogicAppQuery.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Runs (
                    AppName TEXT,
                    WorkflowName TEXT,
                    RunName TEXT,
                    Status TEXT,
                    StartTime TEXT,
                    Content TEXT,
                    PRIMARY KEY (AppName, WorkflowName, RunName)
                );
            ";
            await command.ExecuteNonQueryAsync();
        }

        var cache = new RunCache(connection, appName, workflowName);

        await MigrateLegacyCacheAsync(connection, dir, appName, workflowName);

        return cache;
    }

    private static async Task MigrateLegacyCacheAsync(SqliteConnection connection, string dir, string appName, string workflowName)
    {
        // Lazy migration
        var fileName = Sanitize(appName) + "-" + Sanitize(workflowName) + ".cache.json";
        var legacyFilePath = Path.Combine(dir, fileName);

        if (File.Exists(legacyFilePath))
        {
            try
            {
                Dictionary<string, CachedRun>? dict;
                await using (var stream = File.OpenRead(legacyFilePath))
                {
                    dict = await JsonSerializer.DeserializeAsync<Dictionary<string, CachedRun>>(stream);
                }

                if (dict is not null && dict.Count > 0)
                {
                    using var transaction = connection.BeginTransaction();

                    using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = @"
                            INSERT OR IGNORE INTO Runs (AppName, WorkflowName, RunName, Status, StartTime, Content)
                            VALUES ($AppName, $WorkflowName, $RunName, $Status, $StartTime, $Content);
                        ";

                        var appNameParam = command.Parameters.Add("$AppName", SqliteType.Text);
                        var workflowNameParam = command.Parameters.Add("$WorkflowName", SqliteType.Text);
                        var runNameParam = command.Parameters.Add("$RunName", SqliteType.Text);
                        var statusParam = command.Parameters.Add("$Status", SqliteType.Text);
                        var startTimeParam = command.Parameters.Add("$StartTime", SqliteType.Text);
                        var contentParam = command.Parameters.Add("$Content", SqliteType.Text);

                        appNameParam.Value = appName;
                        workflowNameParam.Value = workflowName;

                        command.Prepare();

                        foreach (var kvp in dict)
                        {
                            runNameParam.Value = kvp.Key;
                            statusParam.Value = kvp.Value.Status;
                            startTimeParam.Value = kvp.Value.StartTime.ToString("o"); // ISO 8601
                            contentParam.Value = ProtectContent(kvp.Value.Content);
                            await command.ExecuteNonQueryAsync();
                        }
                    }

                    transaction.Commit();
                }

                File.Delete(legacyFilePath);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning:[/] Could not migrate legacy cache file. Starting fresh for this app/workflow. ({Markup.Escape(ex.Message)})");
            }
        }
    }

    private readonly System.Threading.SemaphoreSlim _dbLock = new(1, 1);

    private void AddPrimaryKeyParameters(SqliteCommand command, string runName)
    {
        command.Parameters.AddWithValue("$AppName", _appName);
        command.Parameters.AddWithValue("$WorkflowName", _workflowName);
        command.Parameters.AddWithValue("$RunName", runName);
    }

    public async Task<CachedRun?> TryGetAsync(string runName)
    {
        await _dbLock.WaitAsync();
        try
        {
            using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT Status, StartTime, Content
            FROM Runs
            WHERE AppName = $AppName AND WorkflowName = $WorkflowName AND RunName = $RunName;
        ";
        AddPrimaryKeyParameters(command, runName);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var status = reader.GetString(0);
            var startTimeStr = reader.GetString(1);
            var content = UnprotectContent(reader.GetString(2));

            if (DateTimeOffset.TryParse(startTimeStr, out var startTime))
            {
                return new CachedRun(status, startTime, content);
            }
        }

        return null;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task SetAsync(string runName, CachedRun run)
    {
        await _dbLock.WaitAsync();
        try
        {
            using var command = _connection.CreateCommand();
        command.CommandText = @"
            INSERT OR REPLACE INTO Runs (AppName, WorkflowName, RunName, Status, StartTime, Content)
            VALUES ($AppName, $WorkflowName, $RunName, $Status, $StartTime, $Content);
        ";
        AddPrimaryKeyParameters(command, runName);
        command.Parameters.AddWithValue("$Status", run.Status);
        command.Parameters.AddWithValue("$StartTime", run.StartTime.ToString("o"));
        command.Parameters.AddWithValue("$Content", ProtectContent(run.Content));

        await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    internal static string ProtectContent(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;

        if (!OperatingSystem.IsWindows())
            return content;

        var plainBytes = Encoding.UTF8.GetBytes(content);
        var protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    internal static string UnprotectContent(string content)
    {
        if (string.IsNullOrEmpty(content)) return content;

        if (!OperatingSystem.IsWindows())
            return content;

        try
        {
            var protectedBytes = Convert.FromBase64String(content);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Failed to unprotect content due to invalid format.", ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        return _connection.DisposeAsync();
    }

    private static readonly System.Buffers.SearchValues<char> InvalidFileNameSearchValues =
        System.Buffers.SearchValues.Create(
            Path.GetInvalidFileNameChars().Concat(new[] { '.', '/', '\\' }).Distinct().ToArray());

    internal static string Sanitize(string name)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));

        if (name.AsSpan().IndexOfAny(InvalidFileNameSearchValues) < 0)
        {
            return name;
        }

        return string.Create(name.Length, name, (span, state) =>
        {
            for (int i = 0; i < state.Length; i++)
            {
                char c = state[i];
                span[i] = InvalidFileNameSearchValues.Contains(c) ? '_' : c;
            }
        });
    }
}
