using System.Runtime.CompilerServices;

namespace LogicAppQuery;

internal interface IArmClient
{
    Task<string> DiscoverResourceGroupAsync(string subscriptionId, string appName, CancellationToken ct);

    IAsyncEnumerable<WorkflowRun> ListRunsAsync(
        string subscriptionId,
        string resourceGroup,
        string appName,
        string workflowName,
        DateTimeOffset? start,
        DateTimeOffset? end,
        CancellationToken ct = default);

    IAsyncEnumerable<WorkflowAction> ListActionsAsync(
        string subscriptionId,
        string resourceGroup,
        string appName,
        string workflowName,
        string runName,
        CancellationToken ct = default);

    Task<string?> FetchContentAsync(ContentLink link, CancellationToken ct);
}
