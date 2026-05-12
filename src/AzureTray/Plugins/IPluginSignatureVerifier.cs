namespace AzureTray.Plugins;

public interface IPluginSignatureVerifier
{
    SignatureVerdict Verify(string filePath);
}
