using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Xunit;
using LogicAppQuery;

namespace LogicAppQuery.Tests
{
    public class ODataFilterInjectionTest
    {
        private class MockCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                return new AccessToken("dummy-token", DateTimeOffset.UtcNow.AddHours(1));
            }
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                return new ValueTask<AccessToken>(new AccessToken("dummy-token", DateTimeOffset.UtcNow.AddHours(1)));
            }
        }

        private class MockHandler : HttpMessageHandler
        {
            public string? LastRequestUri { get; private set; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequestUri = request.RequestUri?.AbsoluteUri;
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                // Return empty list so it fails at the end but we can capture the URL
                response.Content = new StringContent("{\"value\":[]}");
                return Task.FromResult(response);
            }
        }

        [Fact]
        public async Task DiscoverResourceGroupAsync_ThrowsArgumentExceptionForInvalidAppName()
        {
            var handler = new MockHandler();
            var client = new HttpClient(handler);
            var armClient = new ArmClient(new MockCredential(), client);

            await Assert.ThrowsAsync<ArgumentException>("appName", async () =>
            {
                await armClient.DiscoverResourceGroupAsync("sub-id", "my'app", CancellationToken.None);
            });

            // Ensure no request was actually sent
            Assert.Null(handler.LastRequestUri);
        }

        [Fact]
        public async Task DiscoverResourceGroupAsync_FormatsFilterCorrectlyForValidAppName()
        {
            var handler = new MockHandler();
            var client = new HttpClient(handler);
            var armClient = new ArmClient(new MockCredential(), client);

            try
            {
                await armClient.DiscoverResourceGroupAsync("sub-id", "my-valid-app123", CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // Expected because the mock returns empty list
            }

            Assert.NotNull(handler.LastRequestUri);

            // Uri.ToString() unescapes some characters in some .NET versions.
            // Better to check AbsoluteUri which keeps things escaped or compare manually.
            var expectedSubstring = "name%20eq%20%27my-valid-app123%27%20and%20resourceType%20eq%20%27Microsoft.Web%2Fsites%27";
            Assert.Contains(expectedSubstring, handler.LastRequestUri);
        }
    }
}
