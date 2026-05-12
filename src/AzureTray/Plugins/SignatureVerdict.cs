namespace AzureTray.Plugins;

public sealed record SignatureVerdict(bool IsSigned, string? SignerThumbprint, string? Subject)
{
    public static SignatureVerdict NotSigned { get; } = new(false, null, null);
}
