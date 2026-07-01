# Logic App Query

A command-line interface (CLI) tool designed to search Azure Logic App Standard workflow runs. It scans through trigger inputs, trigger outputs, action inputs, and action outputs to find a given search term.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- An active Azure Subscription
- [Azure CLI](https://docs.microsoft.com/en-us/cli/azure/install-azure-cli) (recommended for authentication)

## Authentication

The tool authenticates against Azure automatically. It attempts to acquire credentials in the following order:
1. **Azure CLI** (via `az login`) - This is the preferred method for local execution.
2. **Azure PowerShell**
3. **DefaultAzureCredential** (Useful for CI/CD, Managed Identities, and environment variables).

If your subscription belongs to a non-default Azure tenant, you can explicitly specify the tenant ID using the `--tenant` or `-t` flag.

## Usage

To run the tool, you can use the `dotnet run` command or build the executable and run it directly.

```bash
dotnet run -- --subscription <SUBSCRIPTION_ID> --app <LOGIC_APP_NAME> --workflow <WORKFLOW_NAME> --search <SEARCH_TERM>
```

### Options

| Option | Short | Required | Description |
|--------|-------|----------|-------------|
| `--subscription` | `-s` | **Yes** | The Azure subscription ID containing the Logic App. |
| `--app` | `-a` | **Yes** | The name of the Logic App Standard site. |
| `--workflow` | `-w` | **Yes** | The name of the specific workflow to search within. |
| `--search` | `-q` | **Yes** | The search term to find in the trigger and action inputs/outputs. |
| `--start` | | No | Filter: include runs starting on or after this time (ISO 8601, e.g. `2024-01-01`). |
| `--end` | | No | Filter: include runs starting on or before this time (ISO 8601, e.g. `2024-12-31`). |
| `--tenant` | `-t` | No | Azure tenant ID (required when the subscription belongs to a non-default tenant). |

### Example

Search for the term `"error-code-500"` in a workflow named `ProcessOrder` within the Logic App `my-logic-app` in the last 7 days:

```bash
dotnet run -- -s "00000000-0000-0000-0000-000000000000" -a "my-logic-app" -w "ProcessOrder" -q "error-code-500" --start "2024-10-01T00:00:00Z"
```

## Caching

To optimize performance and reduce the number of requests made to the Azure ARM API, `LogicAppQuery` employs local caching for workflow runs.

When a workflow run has reached a **terminal state** (e.g., `Succeeded`, `Failed`, `Cancelled`, `Skipped`, `TimedOut`, `Aborted`), its content is cached locally. On subsequent searches for the same Logic App and Workflow, the tool will retrieve the payload from the cache instead of fetching it from Azure again, significantly speeding up the search process.

Caches are stored in your local application data folder (`%LOCALAPPDATA%\LogicAppQuery` on Windows or `~/.local/share/LogicAppQuery` on Linux/macOS).
