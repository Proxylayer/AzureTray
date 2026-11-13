using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// Rendering of an eligible-role row whose activation request is sitting with an
// approver: the row greys out with a "(pending)" suffix so the same role can't
// be requested twice. The pending entry is seeded through the store's own file
// (pending-activations-{tenantId}.json) before the plugin boots — exactly how a
// request submitted in a previous session reaches a fresh start.
public sealed class PimPluginPendingRowTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "azuretray-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task PendingRow_IsGreyedOut_WithThePendingSuffix()
    {
        SeedPendingStore(PendingEntry(roleDefinitionId: "role-owner", directoryScopeId: "/"));

        var row = await RoleRowAsync();

        Assert.Equal("Owner (Entra ID directory) (pending)", row.Text);
        Assert.False(row.IsEnabled);
        Assert.Null(row.Invoke);
        Assert.Equal("⏳", row.Icon);
        Assert.NotNull(row.ContextItems);
        Assert.Collection(
            row.ContextItems!,
            first => Assert.Equal("Copy role name", first.Text),
            second => Assert.Equal("Cancel request and request again…", second.Text));
    }

    // Ctrl+click on the greyed row is the shortcut for the "Cancel request and
    // request again…" context item — the host fires CtrlInvoke even though
    // IsEnabled is false, so the pending row must carry one.
    [Fact]
    public async Task PendingRow_CarriesACtrlInvoke()
    {
        SeedPendingStore(PendingEntry(roleDefinitionId: "role-owner", directoryScopeId: "/"));

        var row = await RoleRowAsync();

        Assert.NotNull(row.CtrlInvoke);
    }

    // A null persisted scope and the row's "/" both mean the whole directory.
    [Fact]
    public async Task PendingRow_NullDirectoryScope_StillMatchesTheDirectoryWideRow()
    {
        SeedPendingStore(PendingEntry(roleDefinitionId: "role-owner", directoryScopeId: null));

        var row = await RoleRowAsync();

        Assert.Equal("Owner (Entra ID directory) (pending)", row.Text);
        Assert.False(row.IsEnabled);
    }

    // An entry persisted by a build before RoleDefinitionId existed carries
    // null there and must never grey a row — it stays clickable.
    [Fact]
    public async Task LegacyPendingEntry_LeavesTheRowClickable()
    {
        SeedPendingStore(PendingEntry(roleDefinitionId: null, directoryScopeId: null));

        var row = await RoleRowAsync();

        // No policy is readable in this harness, so the cap suffix is the
        // service's own 8h ceiling on Entra activations.
        Assert.Equal("Owner (Entra ID directory) (max 8h)", row.Text);
        Assert.True(row.IsEnabled);
        Assert.NotNull(row.Invoke);
    }

    // Cancel-and-resubmit belongs to pending rows only: a clickable row keeps a
    // bare context menu and no Ctrl+click action.
    [Fact]
    public async Task ClickableRow_HasNoCtrlInvoke_AndOnlyTheCopyContextItem()
    {
        var row = await RoleRowAsync();

        Assert.True(row.IsEnabled);
        Assert.Null(row.CtrlInvoke);
        Assert.NotNull(row.ContextItems);
        var context = Assert.Single(row.ContextItems!);
        Assert.Equal("Copy role name", context.Text);
    }

    // A pending entry for some other role leaves this row alone.
    [Fact]
    public async Task PendingEntryForADifferentRole_LeavesTheRowClickable()
    {
        SeedPendingStore(PendingEntry(roleDefinitionId: "role-reader", directoryScopeId: "/"));

        var row = await RoleRowAsync();

        Assert.Equal("Owner (Entra ID directory) (max 8h)", row.Text);
        Assert.True(row.IsEnabled);
    }

    // ---- harness ----------------------------------------------------------

    private static PendingActivationRequest PendingEntry(string? roleDefinitionId, string? directoryScopeId)
        => new(
            Source: PimSource.EntraId,
            RequestId: "req-pending-1",
            RoleName: "Owner",
            ScopeDisplay: "Entra ID directory",
            ArmScope: null,
            SubmittedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            RoleDefinitionId: roleDefinitionId,
            DirectoryScopeId: directoryScopeId);

    // Writes the store file the way PendingActivationStore itself does (default
    // System.Text.Json options), before the plugin ever boots.
    private void SeedPendingStore(params PendingActivationRequest[] entries)
    {
        Directory.CreateDirectory(_dataDir);
        var path = Path.Combine(_dataDir, "pending-activations-tenant-1.json");
        File.WriteAllText(path, JsonSerializer.Serialize(entries));
    }

    private const string NoActivesJson = """{ "value": [] }""";

    private const string EligibleJson = """
        { "value": [ {
            "id": "elig-1",
            "principalId": "prin-1",
            "roleDefinitionId": "role-owner",
            "directoryScopeId": "/",
            "roleDefinition": { "id": "role-owner", "displayName": "Owner" }
        } ] }
        """;

    // Boots the plugin against canned Graph/ARM responses (one Entra eligible
    // role, no actives), forces the eligible-roles poll through the plugin's
    // own Test Runner entry, and returns the single Entra role row from the
    // Open Request menu.
    private async Task<PluginMenuItem> RoleRowAsync()
    {
        using var plugin = new AzureTray.Plugin.PIM.PimPlugin();
        var context = NewContext();

        await plugin.InitializeAsync(context, CancellationToken.None);
        try
        {
            var poll = plugin.Tests.Single(t => t.Name == "Force eligible-roles poll");
            var result = await poll.Run(CancellationToken.None);
            Assert.True(result.Passed, result.Message);

            var openRequest = plugin.GetMenuItems()[1];
            Assert.NotNull(openRequest.SearchProvider);
            return openRequest.SearchProvider!(string.Empty).Single(
                c => c.Text.Contains("Owner", StringComparison.Ordinal));
        }
        finally
        {
            await plugin.ShutdownAsync(CancellationToken.None);
        }
    }

    private IPluginContext NewContext()
    {
        var tenants = new[] { new PluginTenant("tenant-1", "Contoso") };

        var ctx = Substitute.For<IPluginContext>();
        ctx.Logger.Returns(NullLogger<PimPluginPendingRowTests>.Instance);
        ctx.Tenants.Returns(tenants);
        ctx.ReadyTenants.Returns(tenants);
        ctx.Notifier.Returns(Substitute.For<INotifier>());
        ctx.GraphScope.Returns("https://graph.microsoft.com/.default");
        ctx.ArmScope.Returns("https://management.azure.com/.default");
        ctx.DataDir.Returns(_dataDir);
        ctx.GetHttpClient(Arg.Any<string>()).Returns(new StubPluginHttp());
        return ctx;
    }

    // Canned Graph/ARM responses: one Entra eligible role, no actives, and
    // empty everything else. The "roleAssignmentScheduleRequests" reply also
    // answers the pending-activation status poll — an empty object carries no
    // status, so the tracked entry stays tracked (status null → retry), which
    // is exactly the state these tests render.
    private sealed class StubPluginHttp : IPluginHttpClient
    {
        public Task<HttpResponseMessage> SendAsync(
            string clientName, string scope, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Both PIM clients build relative URIs, so ToString() is the only
            // safe accessor here (AbsoluteUri throws on a relative Uri).
            var url = request.RequestUri?.ToString() ?? string.Empty;
            var json = Reply(url);
            return Task.FromResult(new HttpResponseMessage(
                json is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                Content = new StringContent(json ?? "{}", Encoding.UTF8, "application/json"),
            });
        }

        private static string? Reply(string url)
        {
            if (url.Contains("v1.0/me", StringComparison.Ordinal)) return """{ "id": "prin-1" }""";
            if (url.Contains("roleEligibilitySchedules", StringComparison.Ordinal)) return EligibleJson;
            if (url.Contains("roleAssignmentScheduleInstances", StringComparison.Ordinal)) return NoActivesJson;
            if (url.Contains("roleAssignmentScheduleRequests", StringComparison.Ordinal)) return NoActivesJson;
            if (url.Contains("subscriptions?api-version", StringComparison.Ordinal)) return NoActivesJson;
            return null;
        }
    }
}
