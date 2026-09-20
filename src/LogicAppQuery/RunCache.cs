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

    private static readonly System.Buffers.SearchValues<char> InvalidFileNameChars =
        System.Buffers.SearchValues.Create(
            Path.GetInvalidFileNameChars()
                .Concat(new[] { '.', '/', '\\' })
                .Distinct()
                .ToArray());

    readonly SqliteConnection _connection;
    readonly string _appName;
    readonly string _workflowName;
    readonly string _directory;

    private RunCache(SqliteConnection connection, string appName, string workflowName, string directory)
    {
        _connection = connection;
        _appName = appName;
        _workflowName = workflowName;
        _directory = directory;
    }

    public static bool IsTerminal(string status) => TerminalStates.Contains(status);

    public static async Task<RunCache> LoadAsync(string appName, string workflowName, string? directory = null)
    {
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LogicAppQuery");
        EnsureDirectoryPermissions(dir);

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

        var cache = new RunCache(connection, appName, workflowName, dir);

        await MigrateLegacyCacheAsync(connection, dir, appName, workflowName);

        return cache;
    }

    private static async Task MigrateLegacyCacheAsync(SqliteConnection connection, string dir, string appName, string workflowName)
    {
        // Lazy migration
        var fileName = Sanitize(appName) + "-" + Sanitize(workflowName) + ".cache.json";
        var legacyFilePath = Path.Combine(dir, fileName);

        if (!File.Exists(legacyFilePath))
        {
            return;
        }

        try
        {
            Dictionary<string, CachedRun>? dict;
            await using (var stream = File.OpenRead(legacyFilePath))
            {
                dict = await JsonSerializer.DeserializeAsync<Dictionary<string, CachedRun>>(stream);
            }

            if (dict is not null && dict.Count > 0)
            {
                await MigrateDictionaryAsync(connection, appName, workflowName, dict, dir);
            }

            File.Delete(legacyFilePath);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning:[/] Could not migrate legacy cache file. Starting fresh for this app/workflow. ({Markup.Escape(ex.Message)})");
        }
    }

    private static async Task MigrateDictionaryAsync(
        SqliteConnection connection,
        string appName,
        string workflowName,
        Dictionary<string, CachedRun> dict,
        string dir)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT OR IGNORE INTO Runs (AppName, WorkflowName, RunName, Status, StartTime, Content)
            VALUES ($AppName, $WorkflowName, $RunName, $Status, $StartTime, $Content);
        ";

        var (runNameParam, statusParam, startTimeParam, contentParam) = CreateMigrationParameters(command, appName, workflowName);
        command.Prepare();

        foreach (var (runName, run) in dict)
        {
            runNameParam.Value = runName;
            statusParam.Value = run.Status;
            startTimeParam.Value = run.StartTime.ToString("o"); // ISO 8601
            contentParam.Value = ProtectContent(run.Content, dir);
            await command.ExecuteNonQueryAsync();
        }

        transaction.Commit();
    }

    private static (SqliteParameter RunName, SqliteParameter Status, SqliteParameter StartTime, SqliteParameter Content)
        CreateMigrationParameters(SqliteCommand command, string appName, string workflowName)
    {
        var appNameParam = command.Parameters.Add("$AppName", SqliteType.Text);
        var workflowNameParam = command.Parameters.Add("$WorkflowName", SqliteType.Text);
        var runNameParam = command.Parameters.Add("$RunName", SqliteType.Text);
        var statusParam = command.Parameters.Add("$Status", SqliteType.Text);
        var startTimeParam = command.Parameters.Add("$StartTime", SqliteType.Text);
        var contentParam = command.Parameters.Add("$Content", SqliteType.Text);

        appNameParam.Value = appName;
        workflowNameParam.Value = workflowName;

        return (runNameParam, statusParam, startTimeParam, contentParam);
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
            var content = UnprotectContent(reader.GetString(2), _directory);

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
        command.Parameters.AddWithValue("$Content", ProtectContent(run.Content, _directory));

        await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private static readonly object KeyLock = new();

    internal static byte[] GetOrCreateKey(string? keyDirectory = null)
    {
        var dir = keyDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LogicAppQuery");
        Directory.CreateDirectory(dir);

        var keyFilePath = Path.Combine(dir, "cache.key");

        lock (KeyLock)
        {
            if (File.Exists(keyFilePath))
            {
                var bytes = File.ReadAllBytes(keyFilePath);
                if (bytes.Length == 32)
                {
                    return bytes;
                }
            }

            var key = new byte[32];
            RandomNumberGenerator.Fill(key);
            File.WriteAllBytes(keyFilePath, key);

            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(keyFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch
                {
                    // Ignore if Unix file modes are not supported
                }
            }

            return key;
        }
    }

    internal static string ProtectContent(string content, string? keyDirectory = null)
    {
        if (string.IsNullOrEmpty(content)) return content;

        if (OperatingSystem.IsWindows())
        {
            var plainBytes = Encoding.UTF8.GetBytes(content);
            var protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        var key = GetOrCreateKey(keyDirectory);
        using var aes = new AesGcm(key, 16);

        var plainTextBytes = Encoding.UTF8.GetBytes(content);
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        var cipherText = new byte[plainTextBytes.Length];
        var tag = new byte[16];

        aes.Encrypt(nonce, plainTextBytes, cipherText, tag);

        var result = new byte[nonce.Length + tag.Length + cipherText.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherText, 0, result, nonce.Length + tag.Length, cipherText.Length);

        return Convert.ToBase64String(result);
    }

    internal static string UnprotectContent(string content, string? keyDirectory = null)
    {
        if (string.IsNullOrEmpty(content)) return content;

        if (OperatingSystem.IsWindows())
        {
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

        try
        {
            var data = Convert.FromBase64String(content);
            const int nonceSize = 12;
            const int tagSize = 16;

            if (data.Length < nonceSize + tagSize)
            {
                return content;
            }

            var nonce = data.AsSpan(0, nonceSize);
            var tag = data.AsSpan(nonceSize, tagSize);
            var cipherText = data.AsSpan(nonceSize + tagSize);

            var key = GetOrCreateKey(keyDirectory);
            using var aes = new AesGcm(key, tagSize);

            var plainTextBytes = new byte[cipherText.Length];
            aes.Decrypt(nonce, cipherText, tag, plainTextBytes);

            return Encoding.UTF8.GetString(plainTextBytes);
        }
        catch (Exception)
        {
            return content;
        }
    }

    public ValueTask DisposeAsync()
    {
        return _connection.DisposeAsync();
    }

    internal static void EnsureDirectoryPermissions(string dir)
    {
        const UnixFileMode restrictedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        if (!Directory.Exists(dir))
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(dir, restrictedMode);
            }
            else
            {
                Directory.CreateDirectory(dir);
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dir, restrictedMode);
        }
    }

    internal static string Sanitize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        int firstInvalidIndex = name.AsSpan().IndexOfAny(InvalidFileNameChars);
        if (firstInvalidIndex < 0)
        {
            return name;
        }

        return string.Create(name.Length, name, (span, source) =>
        {
            source.CopyTo(span);
            for (int i = 0; i < span.Length; i++)
            {
                if (InvalidFileNameChars.Contains(span[i]))
                {
                    span[i] = '_';
                }
            }
        });
    }
}
