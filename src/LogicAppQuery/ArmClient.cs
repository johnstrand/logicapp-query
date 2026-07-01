using Azure.Core;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace LogicAppQuery;

internal sealed class ArmClient(TokenCredential credential, HttpClient http) : IArmClient
{
    const string ArmScope = "https://management.azure.com/.default";
    const string ArmBase = "https://management.azure.com";
    const long MaxInputSizeBytes = 5 * 1024 * 1024; // 5 MB

    async ValueTask<string> GetBearerTokenAsync(CancellationToken ct)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext([ArmScope]), ct);
        return token.Token;
    }

    async Task<T> GetArmJsonAsync<T>(string url, CancellationToken ct)
    {
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
            throw new HttpRequestException(
                $"ARM API request failed with status code {(int)resp.StatusCode} ({resp.ReasonPhrase}). " +
                $"URL: {url}\nContent: {content}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct)
            ?? throw new InvalidOperationException("Null response from ARM API.");
    }

    public async Task<string> DiscoverResourceGroupAsync(string subscriptionId, string appName, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"name eq '{appName}' and resourceType eq 'Microsoft.Web/sites'");

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
        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase))
                return parts[i + 1];
        }
        throw new InvalidOperationException($"Could not extract resource group from resource ID: {resourceId}");
    }

    public async IAsyncEnumerable<WorkflowRun> ListRunsAsync(
        string subscriptionId,
        string resourceGroup,
        string appName,
        string workflowName,
        DateTimeOffset? start,
        DateTimeOffset? end,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"{ArmBase}/subscriptions/{Uri.EscapeDataString(subscriptionId)}/resourceGroups/{Uri.EscapeDataString(resourceGroup)}" +
                  $"/providers/Microsoft.Web/sites/{Uri.EscapeDataString(appName)}" +
                  $"/hostruntime/runtime/webhooks/workflow/api/management" +
                  $"/workflows/{Uri.EscapeDataString(workflowName)}/runs?api-version=2018-11-01";

        var filters = new List<string>();
        if (start.HasValue) filters.Add($"StartTime ge {start.Value.UtcDateTime:O}");
        if (end.HasValue) filters.Add($"StartTime le {end.Value.UtcDateTime:O}");
        if (filters.Count > 0)
            url += $"&$filter={Uri.EscapeDataString(string.Join(" and ", filters))}";

        string? nextUrl = url;
        while (nextUrl is not null)
        {
            ct.ThrowIfCancellationRequested();
            var page = await GetArmJsonAsync<RunsListResponse>(nextUrl, ct);
            foreach (var run in page.Value)
                yield return run;
            nextUrl = page.NextLink;
        }
    }

    public async IAsyncEnumerable<WorkflowAction> ListActionsAsync(
        string subscriptionId,
        string resourceGroup,
        string appName,
        string workflowName,
        string runName,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"{ArmBase}/subscriptions/{Uri.EscapeDataString(subscriptionId)}/resourceGroups/{Uri.EscapeDataString(resourceGroup)}" +
                  $"/providers/Microsoft.Web/sites/{Uri.EscapeDataString(appName)}" +
                  $"/hostruntime/runtime/webhooks/workflow/api/management" +
                  $"/workflows/{Uri.EscapeDataString(workflowName)}/runs/{Uri.EscapeDataString(runName)}/actions?api-version=2018-11-01";

        string? nextUrl = url;
        while (nextUrl is not null)
        {
            ct.ThrowIfCancellationRequested();
            var page = await GetArmJsonAsync<ActionListResponse>(nextUrl, ct);
            foreach (var action in page.Value)
                yield return action;
            nextUrl = page.NextLink;
        }
    }

    public async Task<string?> FetchContentAsync(ContentLink link, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(link.Uri)) return null;
        if (link.ContentSize > MaxInputSizeBytes) return null;

        // Blob/file storage SAS URLs don't accept a Bearer header. We can skip the bearer token
        // retrieval and the first failed request attempt if we detect a signature in the URI.
        if (link.Uri.Contains("sig="))
        {
            return await TryFetchAsync(link.Uri, null, ct);
        }

        if (Uri.TryCreate(link.Uri, UriKind.Absolute, out var parsedUri) &&
            parsedUri.Host.Equals("management.azure.com", StringComparison.OrdinalIgnoreCase))
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
        using var resp = await http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode
            ? await resp.Content.ReadAsStringAsync(ct)
            : null;
    }
}
