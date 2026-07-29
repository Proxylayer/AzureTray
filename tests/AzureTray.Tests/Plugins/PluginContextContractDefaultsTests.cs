using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AzureTray.Plugin.Contracts;
using Xunit;

namespace AzureTray.Tests.Plugins;

// A plugin compiled against the pre-API-4 contract surface never overrides the
// new members. It must still bind, and the default implementations must give it
// the documented "carry on, nothing happened" answers rather than throwing.
public sealed class PluginContextContractDefaultsTests
{
    [Fact]
    public async Task RefreshTokenAsync_DefaultImplementation_ReturnsFalse()
    {
        IPluginContext legacyHostContext = new LegacyHostContext();

        Assert.False(await legacyHostContext.RefreshTokenAsync("tenant-1", CancellationToken.None));
    }

    [Fact]
    public async Task RefreshTokenAsync_DefaultImplementation_CancellationTokenIsOptional()
    {
        IPluginContext legacyHostContext = new LegacyHostContext();

        Assert.False(await legacyHostContext.RefreshTokenAsync("tenant-1"));
    }

    [Fact]
    public void HostVersion_DefaultImplementation_IsNull()
    {
        IPluginContext legacyHostContext = new LegacyHostContext();

        Assert.Null(legacyHostContext.HostVersion);
    }

    // Deliberately implements only the members that existed before API 4 —
    // adding RefreshTokenAsync here would defeat the point of the test.
    private sealed class LegacyHostContext : IPluginContext
    {
        public ILogger Logger => NullLogger.Instance;
        public INotifier Notifier => throw new NotSupportedException();
        public IClipboard Clipboard => throw new NotSupportedException();
        public IReadOnlyList<PluginTenant> Tenants => Array.Empty<PluginTenant>();
        public IReadOnlyList<PluginTenant> ReadyTenants => Array.Empty<PluginTenant>();
        public string GraphScope => "https://graph.microsoft.com/.default";
        public string ArmScope => "https://management.azure.com/.default";
        public string DataDir => string.Empty;

        public IPluginHttpClient GetHttpClient(string tenantId) => throw new NotSupportedException();

        public bool IsTenantReady(string tenantId) => false;

        public event Action<PluginTenant> TenantReady { add { } remove { } }
        public event Action<string> TenantRemoved { add { } remove { } }
    }
}
