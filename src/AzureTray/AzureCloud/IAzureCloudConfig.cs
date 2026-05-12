using System;

namespace AzureTray.AzureCloud;

public interface IAzureCloudConfig
{
    Uri Authority { get; }
    Uri GraphEndpoint { get; }
    Uri ArmEndpoint { get; }
    string GraphScope { get; }
    string ArmScope { get; }
}
