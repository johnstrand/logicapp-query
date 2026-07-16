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

    [Fact]
    public async Task DiscoverResourceGroupAsync_InvalidAppName_ThrowsArgumentException()
    {
        // Arrange
        var handler = new MockHttpMessageHandler();
        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.DiscoverResourceGroupAsync("sub-id", "invalid_app_name!", CancellationToken.None));

        Assert.Contains("Invalid app name format", ex.Message);
        Assert.Empty(handler.Requests); // No HTTP requests should have been made
    }

    [Fact]
    public async Task DiscoverResourceGroupAsync_ValidInput_FindsResource()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(req =>
        {
            var content = """
            {
                "value": [
                    {
                        "id": "/subscriptions/sub-id/resourceGroups/test-rg/providers/Microsoft.Web/sites/test-app",
                        "kind": "functionapp,workflowapp"
                    }
                ]
            }
            """;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            };
        });
        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));

        // Act
        var result = await client.DiscoverResourceGroupAsync("sub-id", "test-app", CancellationToken.None);

        // Assert
        Assert.Equal("test-rg", result);
        Assert.Single(handler.Requests);
        Assert.Contains("$filter=name eq %27test-app%27 and resourceType eq %27Microsoft.Web%2Fsites%27", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task DiscoverResourceGroupAsync_MultipleResources_PicksWorkflowApp()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(req =>
        {
            var content = """
            {
                "value": [
                    {
                        "id": "/subscriptions/sub-id/resourceGroups/wrong-rg/providers/Microsoft.Web/sites/test-app",
                        "kind": "app"
                    },
                    {
                        "id": "/subscriptions/sub-id/resourceGroups/correct-rg/providers/Microsoft.Web/sites/test-app",
                        "kind": "functionapp,workflowapp"
                    }
                ]
            }
            """;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            };
        });
        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));

        // Act
        var result = await client.DiscoverResourceGroupAsync("sub-id", "test-app", CancellationToken.None);

        // Assert
        Assert.Equal("correct-rg", result);
    }

    [Fact]
    public async Task DiscoverResourceGroupAsync_Pagination_FollowsNextLink()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("next-page"))
            {
                var page2 = """
                {
                    "value": [
                        {
                            "id": "/subscriptions/sub-id/resourceGroups/test-rg/providers/Microsoft.Web/sites/test-app",
                            "kind": "workflowapp"
                        }
                    ]
                }
                """;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(page2) };
            }
            else
            {
                var page1 = """
                {
                    "value": [],
                    "nextLink": "https://management.azure.com/next-page"
                }
                """;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(page1) };
            }
        });
        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));

        // Act
        var result = await client.DiscoverResourceGroupAsync("sub-id", "test-app", CancellationToken.None);

        // Assert
        Assert.Equal("test-rg", result);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task DiscoverResourceGroupAsync_EmptyPages_RetriesAndFindsResource()
    {
        // Arrange
        int requestCount = 0;
        var handler = new MockHttpMessageHandler(req =>
        {
            requestCount++;
            if (requestCount == 1) // First attempt
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"value":[]}""")
                };
            }
            else // Second attempt (retry)
            {
                var content = """
                {
                    "value": [
                        {
                            "id": "/subscriptions/sub-id/resourceGroups/retry-rg/providers/Microsoft.Web/sites/test-app",
                            "kind": "workflowapp"
                        }
                    ]
                }
                """;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(content)
                };
            }
        });
        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));

        // Act
        var result = await client.DiscoverResourceGroupAsync("sub-id", "test-app", CancellationToken.None);

        // Assert
        Assert.Equal("retry-rg", result);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task DiscoverResourceGroupAsync_EmptyPages_ThrowsAfter3Attempts()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"value":[]}""")
            };
        });
        var client = new ArmClient(new FakeTokenCredential(), new HttpClient(handler));

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.DiscoverResourceGroupAsync("sub-id", "test-app", CancellationToken.None));

        Assert.Contains("No site named 'test-app' found", ex.Message);
        Assert.Equal(3, handler.Requests.Count); // 3 attempts made
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
}
