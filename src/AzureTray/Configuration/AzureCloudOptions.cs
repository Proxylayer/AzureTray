namespace AzureTray.Configuration;

public sealed class AzureCloudOptions
{
    public const string SectionName = "App:AzureCloud";

    public string Authority { get; init; } = "https://login.microsoftonline.com/";
    public string GraphEndpoint { get; init; } = "https://graph.microsoft.com/";
    public string ArmEndpoint { get; init; } = "https://management.azure.com/";
}
