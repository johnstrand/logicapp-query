using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Xunit;

namespace LogicAppQuery.Tests;

public class ListActionsAsyncTests
{
    private class FakeTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return new ValueTask<AccessToken>(new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>? _responseFactory;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage>? responseFactory = null)
        {
            _responseFactory = responseFactory;
        }

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = _responseFactory != null
                ? _responseFactory(request)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("fake-content") };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task ListActionsAsync_ValidInputs_ReturnsWorkflowActionsAndEscapesUrlCorrectly()
    {
        // Arrange
        var responseContent = JsonSerializer.Serialize(new ActionListResponse(
            new List<WorkflowAction>
            {
                new("action1", new WorkflowActionProperties("Succeeded", null, null, null, null)),
                new("action2", new WorkflowActionProperties("Failed", null, null, null, null))
            },
            null
        ));

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseContent)
        });

        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));

        // Act
        var actions = new List<WorkflowAction>();
        await foreach (var action in client.ListActionsAsync("sub id", "res group", "app name", "workflow name", "run name", CancellationToken.None))
        {
            actions.Add(action);
        }

        // Assert
        Assert.Equal(2, actions.Count);
        Assert.Equal("action1", actions[0].Name);
        Assert.Equal("action2", actions[1].Name);

        Assert.Single(handler.Requests);
        var request = handler.Requests[0];

        // Assert proper escaping
        Assert.Contains("sub%20id", request.RequestUri!.OriginalString);
        Assert.Contains("res%20group", request.RequestUri!.OriginalString);
        Assert.Contains("app%20name", request.RequestUri!.OriginalString);
        Assert.Contains("workflow%20name", request.RequestUri!.OriginalString);
        Assert.Contains("run%20name", request.RequestUri!.OriginalString);
    }
}
