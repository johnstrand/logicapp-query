using System;
using Xunit;
using LogicAppQuery;

namespace LogicAppQuery.Tests;

public class ArmClientTests
{
    [Fact]
    public void ExtractResourceGroup_HappyPath_ReturnsResourceGroup()
    {
        // Arrange
        var resourceId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/my-resource-group/providers/Microsoft.Web/sites/my-app";

        // Act
        var result = ArmClient.ExtractResourceGroup(resourceId);

        // Assert
        Assert.Equal("my-resource-group", result);
    }

    [Fact]
    public void ExtractResourceGroup_CaseInsensitive_ReturnsResourceGroup()
    {
        // Arrange
        var resourceId = "/subscriptions/12345678-1234-1234-1234-123456789012/ReSoUrCeGrOuPs/my-resource-group/providers/Microsoft.Web/sites/my-app";

        // Act
        var result = ArmClient.ExtractResourceGroup(resourceId);

        // Assert
        Assert.Equal("my-resource-group", result);
    }

    [Fact]
    public void ExtractResourceGroup_MissingResourceGroupsSegment_ThrowsInvalidOperationException()
    {
        // Arrange
        var resourceId = "/subscriptions/12345678-1234-1234-1234-123456789012/providers/Microsoft.Web/sites/my-app";

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => ArmClient.ExtractResourceGroup(resourceId));
        Assert.Contains("Could not extract resource group from resource ID", exception.Message);
    }

    [Fact]
    public void ExtractResourceGroup_NullString_ThrowsArgumentNullException()
    {
        // Arrange
        string resourceId = null!;

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => ArmClient.ExtractResourceGroup(resourceId));
        Assert.Equal("resourceId", exception.ParamName);
    }

    [Fact]
    public void ExtractResourceGroup_WhitespaceString_ThrowsInvalidOperationException()
    {
        // Arrange
        var resourceId = "   ";

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => ArmClient.ExtractResourceGroup(resourceId));
        Assert.Contains("Could not extract resource group from resource ID", exception.Message);
    }

    [Fact]
    public void ExtractResourceGroup_EmptyString_ThrowsInvalidOperationException()
    {
        // Arrange
        var resourceId = "";

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => ArmClient.ExtractResourceGroup(resourceId));
        Assert.Contains("Could not extract resource group from resource ID", exception.Message);
    }

    [Fact]
    public void ExtractResourceGroup_ResourceGroupsAsLastSegment_ThrowsInvalidOperationException()
    {
        // Arrange
        var resourceId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups";

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => ArmClient.ExtractResourceGroup(resourceId));
        Assert.Contains("Could not extract resource group from resource ID", exception.Message);
    }

    [Fact]
    public void ExtractResourceGroup_TrailingSlash_ReturnsResourceGroup()
    {
        // Arrange
        var resourceId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/my-resource-group/";

        // Act
        var result = ArmClient.ExtractResourceGroup(resourceId);

        // Assert
        Assert.Equal("my-resource-group", result);
    }

    [Fact]
    public void ExtractResourceGroup_MultipleSlashes_ReturnsResourceGroup()
    {
        // Arrange
        var resourceId = "//subscriptions/12345678-1234-1234-1234-123456789012//resourceGroups///my-resource-group//providers/";

        // Act
        var result = ArmClient.ExtractResourceGroup(resourceId);

        // Assert
        Assert.Equal("my-resource-group", result);
    }
    [Fact]
    public async Task FetchContentAsync_ManagementAzureCom_SendsBearerToken()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));
        var link = new ContentLink("https://management.azure.com/some/path", 100);

        // Act
        await client.FetchContentAsync(link, CancellationToken.None);

        // Assert
        Assert.Single(handler.Requests);
        var req = handler.Requests[0];
        Assert.NotNull(req.Headers.Authorization);
        Assert.Equal("Bearer", req.Headers.Authorization.Scheme);
        Assert.Equal("fake-token", req.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task FetchContentAsync_ArbitraryDomain_ReturnsNullAndMakesNoRequests()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));
        var link = new ContentLink("https://attacker.com/some/path", 100);

        // Act
        var result = await client.FetchContentAsync(link, CancellationToken.None);

        // Assert
        Assert.Null(result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FetchContentAsync_AllowedStorageDomain_MakesRequest()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));
        var link = new ContentLink("https://myaccount.blob.core.windows.net/some/path?sig=123", 100);

        // Act
        await client.FetchContentAsync(link, CancellationToken.None);

        // Assert
        Assert.Single(handler.Requests);
        var req = handler.Requests[0];
        Assert.Null(req.Headers.Authorization);
    }

    [Fact]
    public async Task FetchContentAsync_SigInQuery_DoesNotSendBearerToken()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));
        // Even if it's on management.azure.com, if it has a sig=, it skips the bearer
        var link = new ContentLink("https://management.azure.com/some/path?sig=123", 100);

        // Act
        await client.FetchContentAsync(link, CancellationToken.None);

        // Assert
        Assert.Single(handler.Requests);
        var req = handler.Requests[0];
        Assert.Null(req.Headers.Authorization);
    }

    [Fact]
    public async Task DiscoverResourceGroupAsync_MaliciousNextLink_ThrowsInvalidOperationException()
    {
        // Arrange
        var handler = new MaliciousNextLinkHttpMessageHandler();
        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.DiscoverResourceGroupAsync("sub-id", "my-app", CancellationToken.None));

        Assert.Contains("Invalid ARM API URL", exception.Message);
    }

    private class FakeTokenCredential : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return new Azure.Core.AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return new ValueTask<Azure.Core.AccessToken>(new Azure.Core.AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    [Fact]
    public async Task DiscoverResourceGroupAsync_FailedRequest_TruncatesContentInExceptionMessage()
    {
        // Arrange
        var longContent = new string('A', 300);
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
        {
            Content = new StringContent(longContent)
        });
        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));

        // Act & Assert
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.DiscoverResourceGroupAsync("sub-id", "test-app", CancellationToken.None));

        Assert.Contains("Content: " + new string('A', 256) + "...", ex.Message);
        Assert.DoesNotContain(new string('A', 257), ex.Message);
    }

    private class MockHttpMessageHandler : System.Net.Http.HttpMessageHandler
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
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("fake-content") };
            return Task.FromResult(response);
        }
    }

    private class MaliciousNextLinkHttpMessageHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.ToString().Contains("api-version=") == true)
            {
                // Return a valid first page response but with a malicious NextLink
                var responseContent = """
                {
                    "value": [],
                    "nextLink": "https://attacker.com/malicious/next/page"
                }
                """;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(responseContent)
                });
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("fake-content") });
        }
    }

    [Fact]
    public async Task ListActionsAsync_Paginated_ReturnsAllActions()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(request =>
        {
            var uri = request.RequestUri?.ToString();
            if (uri != null && !uri.Contains("next"))
            {
                var responseContent = """
                {
                    "value": [
                        { "name": "Action1", "properties": { "status": "Succeeded" } }
                    ],
                    "nextLink": "https://management.azure.com/next"
                }
                """;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(responseContent)
                };
            }
            else
            {
                var responseContent = """
                {
                    "value": [
                        { "name": "Action2", "properties": { "status": "Failed" } }
                    ],
                    "nextLink": null
                }
                """;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(responseContent)
                };
            }
        });

        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));

        // Act
        var actions = new List<WorkflowAction>();
        await foreach (var action in client.ListActionsAsync("sub1", "rg1", "app1", "wf1", "run1"))
        {
            actions.Add(action);
        }

        // Assert
        Assert.Equal(2, actions.Count);
        Assert.Equal("Action1", actions[0].Name);
        Assert.Equal("Succeeded", actions[0].Properties.Status);
        Assert.Equal("Action2", actions[1].Name);
        Assert.Equal("Failed", actions[1].Properties.Status);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ListActionsAsync_ParametersWithSpecialChars_EscapesUrlCorrectly()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(request =>
        {
            var responseContent = """
            {
                "value": [],
                "nextLink": null
            }
            """;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseContent)
            };
        });

        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));

        var subscriptionId = "sub 1#";
        var resourceGroup = "rg?2";
        var appName = "app/3";
        var workflowName = "wf&4";
        var runName = "run=5";

        // Act
        var actions = new List<WorkflowAction>();
        await foreach (var action in client.ListActionsAsync(subscriptionId, resourceGroup, appName, workflowName, runName))
        {
            actions.Add(action);
        }

        // Assert
        Assert.Single(handler.Requests);
        // Uri.ToString() unescapes some characters like spaces, so we use OriginalString
        var requestUri = handler.Requests[0].RequestUri?.OriginalString;
        Assert.NotNull(requestUri);

        Assert.Contains(Uri.EscapeDataString(subscriptionId), requestUri);
        Assert.Contains(Uri.EscapeDataString(resourceGroup), requestUri);
        Assert.Contains(Uri.EscapeDataString(appName), requestUri);
        Assert.Contains(Uri.EscapeDataString(workflowName), requestUri);
        Assert.Contains(Uri.EscapeDataString(runName), requestUri);
    }
}
