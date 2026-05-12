using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using AzureTray.Plugins;
using Xunit;

namespace AzureTray.Tests.Plugins;

public sealed class AuthenticodePluginSignatureVerifierTests
{
    [Fact]
    public void Verify_OnNonExistentFile_ReturnsNotSigned()
    {
        var verifier = new AuthenticodePluginSignatureVerifier(
            NullLogger<AuthenticodePluginSignatureVerifier>.Instance);

        var verdict = verifier.Verify(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll"));

        Assert.False(verdict.IsSigned);
        Assert.Null(verdict.SignerThumbprint);
    }

    [Fact]
    public void Verify_OnRandomBytes_ReturnsNotSigned()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllBytes(tempFile, new byte[] { 0x4D, 0x5A, 0x00, 0x00, 0xDE, 0xAD, 0xBE, 0xEF });
        try
        {
            var verifier = new AuthenticodePluginSignatureVerifier(
                NullLogger<AuthenticodePluginSignatureVerifier>.Instance);

            var verdict = verifier.Verify(tempFile);

            Assert.False(verdict.IsSigned);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Verify_OnEmptyOrWhitespacePath_ReturnsNotSigned()
    {
        var verifier = new AuthenticodePluginSignatureVerifier(
            NullLogger<AuthenticodePluginSignatureVerifier>.Instance);

        Assert.False(verifier.Verify("").IsSigned);
        Assert.False(verifier.Verify("   ").IsSigned);
    }
}
