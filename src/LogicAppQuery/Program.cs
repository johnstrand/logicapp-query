using System.CommandLine;
using Azure.Identity;
using LogicAppQuery;
using Spectre.Console;

var subscriptionOpt = new Option<string>("--subscription", "-s")
{
    Required = true,
    Description = "Azure subscription ID"
};

var appOpt = new Option<string>("--app", "-a")
{
    Required = true,
    Description = "Logic App Standard site name"
};

var workflowOpt = new Option<string>("--workflow", "-w")
{
    Required = true,
    Description = "Workflow name"
};

var searchOpt = new Option<string>("--search", "-q")
{
    Required = true,
    Description = "Search term to find in trigger inputs"
};

var startOpt = new Option<DateTimeOffset?>("--start")
{
    Description = "Filter: include runs starting on or after this time (ISO 8601, e.g. 2024-01-01)"
};

var endOpt = new Option<DateTimeOffset?>("--end")
{
    Description = "Filter: include runs starting on or before this time (ISO 8601, e.g. 2024-12-31)"
};

var tenantOpt = new Option<string?>("--tenant", "-t")
{
    Description = "Azure tenant ID (required when the subscription belongs to a non-default tenant)"
};

var root = new RootCommand("Search Logic App Standard workflow run trigger inputs for a given term.");
root.Add(subscriptionOpt);
root.Add(appOpt);
root.Add(workflowOpt);
root.Add(searchOpt);
root.Add(startOpt);
root.Add(endOpt);
root.Add(tenantOpt);

root.SetAction(async (ParseResult result, CancellationToken ct) =>
{
    var subscription = result.GetRequiredValue(subscriptionOpt);
    var app          = result.GetRequiredValue(appOpt);
    var workflow     = result.GetRequiredValue(workflowOpt);
    var search       = result.GetRequiredValue(searchOpt);
    var start        = result.GetValue(startOpt);
    var end          = result.GetValue(endOpt);
    var tenant       = result.GetValue(tenantOpt);

    // Use AzureCliCredential first so 'az login' is always preferred over environment
    // variables or Managed Identity (which DefaultAzureCredential would try first).
    // Fall back to DefaultAzureCredential for CI/CD or other environments.
    var cliOptions = tenant is not null
        ? new AzureCliCredentialOptions { TenantId = tenant }
        : new AzureCliCredentialOptions();
    var defaultOptions = tenant is not null
        ? new DefaultAzureCredentialOptions { TenantId = tenant }
        : new DefaultAzureCredentialOptions();

    var credential = new ChainedTokenCredential(
        new AzureCliCredential(cliOptions),
        new AzurePowerShellCredential(),
        new DefaultAzureCredential(defaultOptions));

    using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
    var armClient  = new ArmClient(credential, http);
    var command    = new SearchCommand(armClient);

    try
    {
        await command.ExecuteAsync(subscription, app, workflow, search, start, end, ct);
        return 0;
    }
    catch (OperationCanceledException)
    {
        AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
        return 1;
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
        AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
        return 1;
    }
});

return await root.Parse(args, new ParserConfiguration()).InvokeAsync(new InvocationConfiguration(), CancellationToken.None);
