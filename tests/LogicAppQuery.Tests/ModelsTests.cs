using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using LogicAppQuery;

namespace LogicAppQuery.Tests;

public class ModelsTests
{
    [Fact]
    public void ResourceListResponse_ConstructorAndProperties()
    {
        var items = new List<ResourceItem> { new("id1", "kind1") };
        var response = new ResourceListResponse(items, "nextLinkUrl");

        Assert.Same(items, response.Value);
        Assert.Equal("nextLinkUrl", response.NextLink);
    }

    [Fact]
    public void ResourceItem_ConstructorAndProperties()
    {
        var item = new ResourceItem("someId", "someKind");
        Assert.Equal("someId", item.Id);
        Assert.Equal("someKind", item.Kind);
    }

    [Fact]
    public void RunsListResponse_ConstructorAndProperties()
    {
        var runs = new List<WorkflowRun>();
        var response = new RunsListResponse(runs, "nextRunLink");

        Assert.Same(runs, response.Value);
        Assert.Equal("nextRunLink", response.NextLink);
    }

    [Fact]
    public void WorkflowRun_ConstructorAndProperties()
    {
        var props = new WorkflowRunProperties("Succeeded", DateTimeOffset.UtcNow, null);
        var run = new WorkflowRun("run1", props);

        Assert.Equal("run1", run.Name);
        Assert.Same(props, run.Properties);
    }

    [Fact]
    public void WorkflowRunTrigger_ConstructorAndProperties()
    {
        var contentLink = new ContentLink("https://example.com/trigger", 100);
        var outputs = JsonDocument.Parse("{\"trig\":\"val\"}").RootElement;
        var trigger = new WorkflowRunTrigger(contentLink, outputs);

        Assert.Same(contentLink, trigger.OutputsLink);
        Assert.Equal(outputs.GetRawText(), trigger.Outputs?.GetRawText());
    }

    [Fact]
    public void ContentLink_ConstructorAndProperties()
    {
        var link = new ContentLink("https://example.com", 256);

        Assert.Equal("https://example.com", link.Uri);
        Assert.Equal(256, link.ContentSize);
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
