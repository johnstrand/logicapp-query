namespace LogicAppQuery;

public record SearchOptions(
    string SubscriptionId,
    string AppName,
    string WorkflowName,
    string SearchTerm,
    DateTimeOffset? Start = null,
    DateTimeOffset? End = null);
