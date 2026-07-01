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
}
