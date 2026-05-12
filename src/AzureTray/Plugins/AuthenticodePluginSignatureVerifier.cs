using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace AzureTray.Plugins;

// Extracts the Authenticode signer certificate from a PE file and reports its
// thumbprint. NOTE: this verifies that the file carries a signature whose
// embedded cert can be parsed — it does NOT validate the certificate chain back
// to a trusted root or check revocation. For meaningful trust, callers should
// combine this with a thumbprint allowlist (PluginTrustMode.RequireTrustedPublisher).
public sealed class AuthenticodePluginSignatureVerifier : IPluginSignatureVerifier
{
    private readonly ILogger<AuthenticodePluginSignatureVerifier> _logger;

    public AuthenticodePluginSignatureVerifier(ILogger<AuthenticodePluginSignatureVerifier> logger)
    {
        _logger = logger;
    }

    public SignatureVerdict Verify(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return SignatureVerdict.NotSigned;
        }

        try
        {
#pragma warning disable SYSLIB0057 // X509Certificate.CreateFromSignedFile remains the supported entry point for Authenticode metadata extraction.
            using var rawCert = X509Certificate.CreateFromSignedFile(filePath);
            using var cert = new X509Certificate2(rawCert);
#pragma warning restore SYSLIB0057
            return new SignatureVerdict(true, cert.Thumbprint, cert.Subject);
        }
        catch (CryptographicException ex)
        {
            _logger.LogDebug(ex, "Plugin {Path} has no readable Authenticode signature.", filePath);
            return SignatureVerdict.NotSigned;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read plugin file {Path} while checking signature.", filePath);
            return SignatureVerdict.NotSigned;
        }
    }
}
