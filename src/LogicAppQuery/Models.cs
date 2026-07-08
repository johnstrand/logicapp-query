using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogicAppQuery;

internal interface IPageableResponse<T>
{
    List<T> Value { get; }
    string? NextLink { get; }
}

internal record ResourceListResponse(
    [property: JsonPropertyName("value")] List<ResourceItem> Value,
    [property: JsonPropertyName("nextLink")] string? NextLink
) : IPageableResponse<ResourceItem>;

internal record ResourceItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string? Kind
);

internal record RunsListResponse(
    [property: JsonPropertyName("value")] List<WorkflowRun> Value,
    [property: JsonPropertyName("nextLink")] string? NextLink
) : IPageableResponse<WorkflowRun>;

internal record WorkflowRun(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("properties")] WorkflowRunProperties Properties
);

internal record WorkflowRunProperties(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("startTime")] DateTimeOffset StartTime,
    [property: JsonPropertyName("endTime")] DateTimeOffset? EndTime,
    [property: JsonPropertyName("trigger")] WorkflowRunTrigger? Trigger
);

internal record WorkflowRunTrigger(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("outputsLink")] ContentLink? OutputsLink,
    [property: JsonPropertyName("outputs")] JsonElement? Outputs  // small payloads are inlined here
);

internal record ContentLink(
    [property: JsonPropertyName("uri")] string? Uri,
    [property: JsonPropertyName("contentSize")] long ContentSize
);

internal record ActionListResponse(
    [property: JsonPropertyName("value")] List<WorkflowAction> Value,
    [property: JsonPropertyName("nextLink")] string? NextLink
) : IPageableResponse<WorkflowAction>;

internal record WorkflowAction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("properties")] WorkflowActionProperties Properties
);

internal record WorkflowActionProperties(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("inputsLink")] ContentLink? InputsLink,
    [property: JsonPropertyName("outputsLink")] ContentLink? OutputsLink,
    [property: JsonPropertyName("inputs")] JsonElement? Inputs,
    [property: JsonPropertyName("outputs")] JsonElement? Outputs
);

