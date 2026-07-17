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

public class ListRunsAsyncTests
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
    public async Task ListRunsAsync_ValidInputs_ReturnsWorkflowRunsAndEscapesUrlCorrectly()
    {
        // Arrange
        var responseContent = JsonSerializer.Serialize(new RunsListResponse(
            new List<WorkflowRun>
            {
                new("run1", new WorkflowRunProperties("Succeeded", DateTimeOffset.UtcNow, new WorkflowRunTrigger(null, null))),
                new("run2", new WorkflowRunProperties("Failed", DateTimeOffset.UtcNow, new WorkflowRunTrigger(null, null)))
            },
            null
        ));

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseContent)
        });

        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));

        // Act
        var runs = new List<WorkflowRun>();
        await foreach (var run in client.ListRunsAsync("sub id", "res group", "app name", "workflow name", null, null, CancellationToken.None))
        {
            runs.Add(run);
        }

        // Assert
        Assert.Equal(2, runs.Count);
        Assert.Equal("run1", runs[0].Name);
        Assert.Equal("run2", runs[1].Name);

        Assert.Single(handler.Requests);
        var request = handler.Requests[0];

        // Assert proper escaping
        Assert.Contains("sub%20id", request.RequestUri!.OriginalString);
        Assert.Contains("res%20group", request.RequestUri!.OriginalString);
        Assert.Contains("app%20name", request.RequestUri!.OriginalString);
        Assert.Contains("workflow%20name", request.RequestUri!.OriginalString);
    }
}
