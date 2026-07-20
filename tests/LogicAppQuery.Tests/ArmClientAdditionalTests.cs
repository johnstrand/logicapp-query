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

public class ArmClientAdditionalTests
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
        public int CallCount { get; private set; } = 0;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage>? responseFactory = null)
        {
            _responseFactory = responseFactory;
        }

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            CallCount++;

            if (_responseFactory != null)
            {
                return Task.FromResult(_responseFactory(request));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("fake-content") });
        }
    }

    [Fact]
    public async Task DiscoverResourceGroupAsync_MultipleMatches_PrefersWorkflowAppKind()
    {
        var responseContent = JsonSerializer.Serialize(new ResourceListResponse(
            new List<ResourceItem>
            {
                new ResourceItem("/subscriptions/sub1/resourceGroups/rg1/providers/Microsoft.Web/sites/my-app", "app"),
                new ResourceItem("/subscriptions/sub1/resourceGroups/rg2/providers/Microsoft.Web/sites/my-app", "functionapp,workflowapp")
            },
            null
        ));

        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseContent)
        });

        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));

        var rg = await client.DiscoverResourceGroupAsync("sub1", "my-app", CancellationToken.None);

        Assert.Equal("rg2", rg);
    }

    private class EmptyPageMockHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; } = 0;
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            CallCount++;

            if (CallCount == 1 || CallCount == 3 || CallCount == 5)
            {
                var responseContent = JsonSerializer.Serialize(new ResourceListResponse(
                    new List<ResourceItem>(),
                    "https://management.azure.com/next.url"
                ));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseContent) });
            }
            else
            {
                var responseContent = JsonSerializer.Serialize(new ResourceListResponse(
                    new List<ResourceItem>(),
                    null
                ));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseContent) });
            }
        }
    }

    [Fact]
    public async Task DiscoverResourceGroupAsync_EmptyPages_RetriesAndThrows()
    {
        var handler = new EmptyPageMockHandler();
        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DiscoverResourceGroupAsync("sub1", "my-app", CancellationToken.None));

        Assert.Contains("No site named 'my-app' found", ex.Message);
        Assert.Equal(6, handler.CallCount); // 3 attempts * 2 pages (1 initial + 1 next link) = 6 calls
    }
}
