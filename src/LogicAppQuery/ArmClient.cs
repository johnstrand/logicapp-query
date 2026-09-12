using Azure.Core;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LogicAppQuery;

internal sealed class ArmClient(TokenCredential credential, HttpClient http) : IArmClient
{
    const string ArmScope = "https://management.azure.com/.default";
    const string ArmBase = "https://management.azure.com";
    const long MaxInputSizeBytes = 5 * 1024 * 1024; // 5 MB

    private AccessToken? _cachedToken;
    private readonly SemaphoreSlim _tokenLock = new SemaphoreSlim(1, 1);

    private bool IsTokenValid => _cachedToken.HasValue && _cachedToken.Value.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5);

    async ValueTask<string> GetBearerTokenAsync(CancellationToken ct)
    {
        if (IsTokenValid)
        {
            return _cachedToken!.Value.Token;
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (IsTokenValid)
            {
                return _cachedToken!.Value.Token;
            }

            _cachedToken = await credential.GetTokenAsync(new TokenRequestContext([ArmScope]), ct);
            return _cachedToken.Value.Token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    async Task<T> GetArmJsonAsync<T>(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUri) ||
            !parsedUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
            !parsedUri.Host.Equals("management.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Invalid ARM API URL. URL must be a valid https URL for management.azure.com");
        }

        var bearer = await GetBearerTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException(
                $"Authentication failed (401) calling ARM API. " +
                $"Ensure you are logged in with 'az login' and have access to the subscription. " +
                $"If the subscription is in a non-default tenant, add --tenant <tenantId>.");

        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException(
                $"Access denied (403) calling ARM API. " +
                $"Ensure your account has at least Reader role on the subscription or Logic App resource.");

        if (!resp.IsSuccessStatusCode)
        {
            var content = await resp.Content.ReadAsStringAsync(ct);
            var safeContent = content.Length > 256 ? content[..256] + "..." : content;
            throw new HttpRequestException(
                $"ARM API request failed with status code {(int)resp.StatusCode} ({resp.ReasonPhrase}). " +
                $"URL: {url}\nContent: {safeContent}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct)
            ?? throw new InvalidOperationException("Null response from ARM API.");
    }

    async IAsyncEnumerable<TItem> GetPaginatedAsync<TResponse, TItem>(
        string url,
        [EnumeratorCancellation] CancellationToken ct)
        where TResponse : IPageableResponse<TItem>
    {
        string? nextUrl = url;
        while (nextUrl is not null)
        {
            ct.ThrowIfCancellationRequested();
            var page = await GetArmJsonAsync<TResponse>(nextUrl, ct);
            foreach (var item in page.Value)
                yield return item;
            nextUrl = page.NextLink;
        }
    }

    public async Task<string> DiscoverResourceGroupAsync(string subscriptionId, string appName, CancellationToken ct)
    {
        if (!Regex.IsMatch(appName, @"^[a-zA-Z0-9\-]+$"))
        {
            throw new ArgumentException($"Invalid app name format: {appName}. Only alphanumeric characters and hyphens are allowed.", nameof(appName));
        }

        var escapedAppName = appName.Replace("'", "''");
        var filter = Uri.EscapeDataString($"name eq '{escapedAppName}' and resourceType eq 'Microsoft.Web/sites'");

        // Paginate the resources list — ARM can return empty first pages transiently.
        // Retry up to 3 times to handle eventual-consistency blips.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);

            string? nextUrl = $"{ArmBase}/subscriptions/{Uri.EscapeDataString(subscriptionId)}/resources?$filter={filter}&api-version=2021-04-01";
            while (nextUrl is not null)
            {
                var page = await GetArmJsonAsync<ResourceListResponse>(nextUrl, ct);
                if (page.Value.Count > 0)
                {
                    var match = page.Value.Count > 1
                        ? page.Value.FirstOrDefault(r =>
                              r.Kind?.Contains("workflowapp", StringComparison.OrdinalIgnoreCase) == true)
                          ?? page.Value[0]
                        : page.Value[0];
                    return ExtractResourceGroup(match.Id);
                }
                nextUrl = page.NextLink;
            }
        }

        throw new InvalidOperationException(
            $"No site named '{appName}' found in subscription '{subscriptionId}'. " +
            $"Verify the app name and that your account has access.");
    }

    internal static string ExtractResourceGroup(string resourceId)
    {
        ArgumentNullException.ThrowIfNull(resourceId);

        ReadOnlySpan<char> span = resourceId.AsSpan();
        bool foundResourceGroupSegment = false;

        while (!span.IsEmpty)
        {
            int nextSlash = span.IndexOf('/');
            ReadOnlySpan<char> segment;
            if (nextSlash < 0)
            {
                segment = span;
                span = default;
            }
            else
            {
                segment = span[..nextSlash];
                span = span[(nextSlash + 1)..];
            }

            if (segment.IsEmpty)
                continue;

            if (foundResourceGroupSegment)
            {
                return segment.ToString();
            }

            if (segment.Equals("resourceGroups", StringComparison.OrdinalIgnoreCase))
            {
                foundResourceGroupSegment = true;
            }
        }

        throw new InvalidOperationException($"Could not extract resource group from resource ID: {resourceId}");
    }

    private static string BuildWorkflowBaseUrl(string subscriptionId, string resourceGroup, string appName, string workflowName)
    {
        return $"{ArmBase}/subscriptions/{Uri.EscapeDataString(subscriptionId)}/resourceGroups/{Uri.EscapeDataString(resourceGroup)}" +
               $"/providers/Microsoft.Web/sites/{Uri.EscapeDataString(appName)}" +
               $"/hostruntime/runtime/webhooks/workflow/api/management" +
               $"/workflows/{Uri.EscapeDataString(workflowName)}";
    }

    public IAsyncEnumerable<WorkflowRun> ListRunsAsync(
        string subscriptionId,
        string resourceGroup,
        string appName,
        string workflowName,
        DateTimeOffset? start,
        DateTimeOffset? end,
        CancellationToken ct = default)
    {
        var baseUrl = BuildWorkflowBaseUrl(subscriptionId, resourceGroup, appName, workflowName);
        var url = $"{baseUrl}/runs?api-version=2018-11-01";

        var filters = new List<string>();
        if (start.HasValue) filters.Add($"StartTime ge {start.Value.UtcDateTime:O}");
        if (end.HasValue) filters.Add($"StartTime le {end.Value.UtcDateTime:O}");
        if (filters.Count > 0)
            url += $"&$filter={Uri.EscapeDataString(string.Join(" and ", filters))}";

        return GetPaginatedAsync<RunsListResponse, WorkflowRun>(url, ct);
    }

    public IAsyncEnumerable<WorkflowAction> ListActionsAsync(
        string subscriptionId,
        string resourceGroup,
        string appName,
        string workflowName,
        string runName,
        CancellationToken ct = default)
    {
        var baseUrl = BuildWorkflowBaseUrl(subscriptionId, resourceGroup, appName, workflowName);
        var url = $"{baseUrl}/runs/{Uri.EscapeDataString(runName)}/actions?api-version=2018-11-01";

        return GetPaginatedAsync<ActionListResponse, WorkflowAction>(url, ct);
    }

    private static bool IsAllowedHost(string host)
    {
        return host.Equals("management.azure.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".file.core.windows.net", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string?> FetchContentAsync(ContentLink link, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(link.Uri)) return null;
        if (link.ContentSize > MaxInputSizeBytes) return null;

        if (!Uri.TryCreate(link.Uri, UriKind.Absolute, out var parsedUri) ||
            parsedUri.Scheme != Uri.UriSchemeHttps ||
            !IsAllowedHost(parsedUri.Host))
        {
            return null;
        }

        // Blob/file storage SAS URLs don't accept a Bearer header. We can skip the bearer token
        // retrieval and the first failed request attempt if we detect a signature in the URI.
        if (link.Uri.Contains("sig="))
        {
            return await TryFetchAsync(link.Uri, null, ct);
        }

        if (parsedUri.Host.Equals("management.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            var bearer = await GetBearerTokenAsync(ct);

            // Try with bearer token first (ARM management content URLs).
            // If that fails, try without auth as a fallback.
            return await TryFetchAsync(link.Uri, bearer, ct)
                ?? await TryFetchAsync(link.Uri, null, ct);
        }

        return await TryFetchAsync(link.Uri, null, ct);
    }

    async Task<string?> TryFetchAsync(string uri, string? bearer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        if (bearer is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        if (resp.Content.Headers.ContentLength > MaxInputSizeBytes)
            return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int totalRead = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            totalRead += read;
            if (totalRead > MaxInputSizeBytes)
                return null;
            ms.Write(buffer, 0, read);
        }

        ms.Position = 0;
        using var reader = new StreamReader(ms, System.Text.Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }
}
