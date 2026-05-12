using System;
using Microsoft.Extensions.Options;
using AzureTray.Configuration;

namespace AzureTray.AzureCloud;

public sealed class AzureCloudConfig : IAzureCloudConfig
{
    public AzureCloudConfig(IOptions<AzureCloudOptions> options)
    {
        var value = options.Value;
        Authority = ParseAbsoluteUri(value.Authority, nameof(value.Authority));
        GraphEndpoint = ParseAbsoluteUri(value.GraphEndpoint, nameof(value.GraphEndpoint));
        ArmEndpoint = ParseAbsoluteUri(value.ArmEndpoint, nameof(value.ArmEndpoint));
    }

    public Uri Authority { get; }
    public Uri GraphEndpoint { get; }
    public Uri ArmEndpoint { get; }

    public string GraphScope => DefaultScope(GraphEndpoint);
    public string ArmScope => DefaultScope(ArmEndpoint);

    private static Uri ParseAbsoluteUri(string value, string field)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                $"AzureCloudOptions.{field} must be an absolute URI; got '{value}'.");
        }
        return uri;
    }

    private static string DefaultScope(Uri endpoint)
        => endpoint.GetLeftPart(UriPartial.Authority) + "/.default";
}
