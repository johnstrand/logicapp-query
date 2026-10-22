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
    [Theory]
    [InlineData("discovery")]
    [InlineData("runs")]
    [InlineData("actions")]
    [InlineData("content")]
    public async Task Requests_Throttled_RetrySameRequestUntilSuccessful(string operation)
    {
        var attempts = 0;
        var throttledContent = new List<TrackingContent>();
        var handler = new MockHttpMessageHandler(_ =>
        {
            if (++attempts <= 8)
            {
                var content = new TrackingContent();
                throttledContent.Add(content);
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = content };
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(operation switch
                {
                    "discovery" => """{"value":[{"id":"/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/sites/app","kind":"workflowapp"}]}""",
                    "content" => "searchable payload",
                    _ => """{"value":[]}"""
                })
            };
        });
        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));

        await ExecuteRequestAsync(client, operation, CancellationToken.None);

        Assert.Equal(9, handler.CallCount);
        Assert.Equal(9, handler.Requests.Distinct().Count());
        var firstRequest = handler.Requests[0];
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(firstRequest.RequestUri, request.RequestUri);
            Assert.Equal(firstRequest.Headers.Authorization, request.Headers.Authorization);
            Assert.Equal(HttpMethod.Get, request.Method);
        });
        Assert.All(throttledContent, content => Assert.True(content.Disposed));
    }

    [Fact]
    public async Task Requests_RetryAfter_ActuallyWaitsBeforeRetrying()
    {
        var attempts = 0;
        var handler = new MockHttpMessageHandler(_ =>
        {
            if (++attempts == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("payload") };
        });
        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await ExecuteRequestAsync(client, "content", CancellationToken.None);

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(1));
        Assert.Equal(2, handler.CallCount);
    }

    [Theory]
    [InlineData("discovery")]
    [InlineData("runs")]
    [InlineData("actions")]
    [InlineData("content")]
    public async Task Requests_CancelledDuringRetryWait_StopWithoutAnotherRequest(string operation)
    {
        using var cts = new CancellationTokenSource();
        var handler = new MockHttpMessageHandler(_ =>
        {
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return response;
        });
        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ExecuteRequestAsync(client, operation, cts.Token).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(5, 32)]
    [InlineData(6, 60)]
    [InlineData(int.MaxValue, 60)]
    public void GetRetryDelay_MissingHeader_UsesCappedExponentialBackoff(int retryCount, int seconds)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        Assert.Equal(TimeSpan.FromSeconds(seconds), ArmClient.GetRetryDelay(response, retryCount, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(120, 120)]
    [InlineData(-30, 0)]
    public void GetRetryDelay_HttpDate_UsesServerDate(int offsetSeconds, int expectedSeconds)
    {
        var now = new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(now.AddSeconds(offsetSeconds));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ArmClient.GetRetryDelay(response, 0, now));
    }

    [Fact]
    public void GetRetryDelay_ServerDelta_IsNotCappedByFallbackLimit()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
        Assert.Equal(TimeSpan.FromSeconds(120), ArmClient.GetRetryDelay(response, 0, DateTimeOffset.UtcNow));
    }

    private static async Task ExecuteRequestAsync(ArmClient client, string operation, CancellationToken ct)
    {
        switch (operation)
        {
            case "discovery":
                Assert.Equal("rg", await client.DiscoverResourceGroupAsync("sub", "app", ct));
                break;
            case "runs":
                await foreach (var run in client.ListRunsAsync("sub", "rg", "app", "workflow", null, null, ct)) { }
                break;
            case "actions":
                await foreach (var action in client.ListActionsAsync("sub", "rg", "app", "workflow", "run", ct)) { }
                break;
            case "content":
                Assert.NotNull(await client.FetchContentAsync(
                    new ContentLink("https://storage.blob.core.windows.net/content?sig=test", 10), ct));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private sealed class TrackingContent : StringContent
    {
        public bool Disposed { get; private set; }

        public TrackingContent() : base("throttled") { }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

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
