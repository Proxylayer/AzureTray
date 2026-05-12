using System;
using Microsoft.Extensions.Options;
using AzureTray.AzureCloud;
using AzureTray.Configuration;
using Xunit;

namespace AzureTray.Tests.AzureCloud;

public sealed class AzureCloudConfigTests
{
    [Fact]
    public void PublicCloudDefaults_ProduceExpectedScopes()
    {
        var config = Build(new AzureCloudOptions());

        Assert.Equal(new Uri("https://login.microsoftonline.com/"), config.Authority);
        Assert.Equal(new Uri("https://graph.microsoft.com/"), config.GraphEndpoint);
        Assert.Equal(new Uri("https://management.azure.com/"), config.ArmEndpoint);
        Assert.Equal("https://graph.microsoft.com/.default", config.GraphScope);
        Assert.Equal("https://management.azure.com/.default", config.ArmScope);
    }

    [Fact]
    public void Scope_StripsAnyPathFromEndpointBeforeAppendingDefault()
    {
        var config = Build(new AzureCloudOptions
        {
            GraphEndpoint = "https://graph.microsoft.com/v1.0/",
        });

        Assert.Equal("https://graph.microsoft.com/.default", config.GraphScope);
    }

    [Fact]
    public void Constructor_WithMalformedAuthority_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build(new AzureCloudOptions { Authority = "not a url" }));
        Assert.Contains("Authority", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_WithRelativeUri_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Build(new AzureCloudOptions { GraphEndpoint = "/graph" }));
        Assert.Contains("GraphEndpoint", ex.Message, StringComparison.Ordinal);
    }

    private static AzureCloudConfig Build(AzureCloudOptions options)
        => new(Options.Create(options));
}
