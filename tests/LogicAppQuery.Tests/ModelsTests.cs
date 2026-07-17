using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using LogicAppQuery;

namespace LogicAppQuery.Tests;

public class ModelsTests
{
    [Fact]
    public void ResourceItem_ConstructorAndProperties()
    {
        var item = new ResourceItem("someId", "someKind");
        Assert.Equal("someId", item.Id);
        Assert.Equal("someKind", item.Kind);
    }

    [Fact]
    public void WorkflowRunProperties_ConstructorAndProperties()
    {
        var start = DateTimeOffset.UtcNow;
        var trigger = new WorkflowRunTrigger(null, null);
        var props = new WorkflowRunProperties("Succeeded", start, trigger);

        Assert.Equal("Succeeded", props.Status);
        Assert.Equal(start, props.StartTime);
        Assert.Same(trigger, props.Trigger);
    }

    [Fact]
    public void ActionListResponse_ConstructorAndProperties()
    {
        var actions = new List<WorkflowAction>();
        var response = new ActionListResponse(actions, "nextLink");

        Assert.Same(actions, response.Value);
        Assert.Equal("nextLink", response.NextLink);
    }

    [Fact]
    public void WorkflowAction_ConstructorAndProperties()
    {
        var props = new WorkflowActionProperties("Succeeded", null, null, null, null);
        var action = new WorkflowAction("actionName", props);

        Assert.Equal("actionName", action.Name);
        Assert.Same(props, action.Properties);
    }

    [Fact]
    public void WorkflowActionProperties_ConstructorAndProperties()
    {
        var inputsLink = new ContentLink("uri1", 10);
        var outputsLink = new ContentLink("uri2", 20);
        var inputs = JsonDocument.Parse("{\"in\":\"val\"}").RootElement;
        var outputs = JsonDocument.Parse("{\"out\":\"val\"}").RootElement;

        var props = new WorkflowActionProperties("Failed", inputsLink, outputsLink, inputs, outputs);

        Assert.Equal("Failed", props.Status);
        Assert.Same(inputsLink, props.InputsLink);
        Assert.Same(outputsLink, props.OutputsLink);
        Assert.Equal(inputs.GetRawText(), props.Inputs?.GetRawText());
        Assert.Equal(outputs.GetRawText(), props.Outputs?.GetRawText());
    }
}
