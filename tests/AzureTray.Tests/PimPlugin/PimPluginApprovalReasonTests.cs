using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray.Plugin.Contracts;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// End-to-end (wire JSON → popup) cover for the requestor's reason.
//
// Two things only this level can pin:
//   * the reason is read from the schedule request's own "justification", not
//     from the approval step / stage that carries the APPROVER's decision
//     comment — the fixtures below deliberately ship both, with different text;
//   * both entry points into HandleNewApprovalAsync surface it: the watcher's
//     auto-prompt on a newly-seen approval, and clicking the approval's row in
//     the tray menu.
public sealed class PimPluginApprovalReasonTests : IDisposable
{
    private const string RequestorReason = "REQUESTOR: incident #42 needs Owner on the gateway";
    private const string ApproverComment = "APPROVER: looks fine to me";

    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "azuretray-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task AutoPrompt_ShowsTheRequestorsReason_NotTheApprovalStepsComment()
    {
        var notifier = NewNotifier();

        await WithPolledPluginAsync(EntraFixture, notifier, _ =>
        {
            var popups = ApprovalPopups(notifier);
            Assert.NotEmpty(popups);
            Assert.All(popups, p =>
            {
                Assert.Contains(
                    "Alice Example is requesting Owner on Entra ID directory.",
                    p.Message, StringComparison.Ordinal);
                Assert.Contains($"Reason: \"{RequestorReason}\"", p.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(ApproverComment, p.Message, StringComparison.Ordinal);
                Assert.Null(p.Details);
            });
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ArmAutoPrompt_ShowsTheRequestorsReason_NotTheApprovalStagesComment()
    {
        var notifier = NewNotifier();

        await WithPolledPluginAsync(ArmFixture, notifier, _ =>
        {
            var popups = ApprovalPopups(notifier);
            Assert.NotEmpty(popups);
            Assert.All(popups, p =>
            {
                Assert.Contains(
                    "Bob Example is requesting Contributor on Dev (sub).",
                    p.Message, StringComparison.Ordinal);
                Assert.Contains($"Reason: \"{RequestorReason}\"", p.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(ApproverComment, p.Message, StringComparison.Ordinal);
                Assert.Null(p.Details);
            });
            return Task.CompletedTask;
        });
    }

    // The other entry point: the approval's row in the Pending Approvals menu.
    // Received calls are cleared after the poll, so anything captured here came
    // from the row — the watcher's own auto-prompt cannot fire twice for the
    // same approval (dedupe) and the next poll is 60s out.
    [Fact]
    public async Task MenuRowInvoke_ShowsTheRequestorsReason()
    {
        var notifier = NewNotifier();

        await WithPolledPluginAsync(EntraFixture, notifier, async plugin =>
        {
            notifier.ClearReceivedCalls();

            var approvals = plugin.GetMenuItems()[0];
            Assert.NotNull(approvals.Children);
            var row = approvals.Children!.Single(
                c => c.Text.Contains("Alice Example", StringComparison.Ordinal));
            Assert.NotNull(row.Invoke);

            row.Invoke!();
            await Settle();

            var popups = ApprovalPopups(notifier);
            Assert.NotEmpty(popups);
            Assert.All(popups, p =>
            {
                Assert.Contains($"Reason: \"{RequestorReason}\"", p.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(ApproverComment, p.Message, StringComparison.Ordinal);
            });
        });
    }

    // ---- harness ----------------------------------------------------------

    // Entra pending approval whose schedule request carries the requestor's
    // reason while the expanded approval step carries the approver's comment.
    private static readonly StubHttp EntraFixture = new(
        graphPendingJson: $$"""
            { "value": [ {
                "id": "req-1",
                "status": "PendingApproval",
                "action": "selfActivate",
                "principalId": "other-objectid",
                "roleDefinitionId": "role-owner",
                "directoryScopeId": "/",
                "justification": "{{RequestorReason}}",
                "approvalId": "approval-1",
                "approval": {
                    "id": "approval-1",
                    "steps": [ {
                        "id": "step-1",
                        "status": "InProgress",
                        "justification": "{{ApproverComment}}"
                    } ]
                },
                "principal": {
                    "id": "other-objectid",
                    "displayName": "Alice Example",
                    "userPrincipalName": "alice@contoso.com"
                },
                "roleDefinition": { "id": "role-owner", "displayName": "Owner" }
            } ] }
            """,
        armPendingJson: EmptyJson,
        subscriptionsJson: EmptyJson);

    // Same shape on the ARM side: properties.justification is the requestor's,
    // the approval stage's justification is the approver's.
    private static readonly StubHttp ArmFixture = new(
        graphPendingJson: EmptyJson,
        armPendingJson: $$"""
            { "value": [ {
                "id": "/subscriptions/sub-1/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/req-arm-1",
                "name": "req-arm-1",
                "properties": {
                    "status": "PendingApproval",
                    "principalId": "other-objectid",
                    "roleDefinitionId": "role-contributor",
                    "scope": "/subscriptions/sub-1",
                    "justification": "{{RequestorReason}}",
                    "requestType": "AdminAdd",
                    "approvalId": "/providers/Microsoft.Authorization/roleAssignmentApprovals/appr-arm-1",
                    "approval": {
                        "stages": [ {
                            "properties": {
                                "status": "InProgress",
                                "justification": "{{ApproverComment}}",
                                "assignedToMe": true
                            }
                        } ]
                    },
                    "expandedProperties": {
                        "principal": { "id": "other-objectid", "displayName": "Bob Example", "type": "User" },
                        "roleDefinition": { "id": "role-contributor", "displayName": "Contributor" },
                        "scope": { "id": "/subscriptions/sub-1", "displayName": "Dev (sub)", "type": "subscription" }
                    }
                }
            } ] }
            """,
        subscriptionsJson: """
            { "value": [ {
                "id": "/subscriptions/sub-1",
                "subscriptionId": "sub-1",
                "displayName": "Dev",
                "state": "Enabled"
            } ] }
            """);

    private const string EmptyJson = """{ "value": [] }""";

    // Boots the plugin against the fixture, forces one pending-approval poll
    // through the plugin's own Test Runner entry, lets the fire-and-forget
    // notification tasks settle, then hands the plugin to the assertions.
    private async Task WithPolledPluginAsync(
        StubHttp http, INotifier notifier, Func<AzureTray.Plugin.PIM.PimPlugin, Task> assert)
    {
        using var plugin = new AzureTray.Plugin.PIM.PimPlugin();
        await plugin.InitializeAsync(NewContext(http, notifier), CancellationToken.None);
        try
        {
            var poll = plugin.Tests.Single(t => t.Name == "Force pending-approval poll");
            var result = await poll.Run(CancellationToken.None);
            Assert.True(result.Passed, result.Message);
            await Settle();

            await assert(plugin);
        }
        finally
        {
            await plugin.ShutdownAsync(CancellationToken.None);
        }
    }

    private IPluginContext NewContext(StubHttp http, INotifier notifier)
    {
        var tenants = new[] { new PluginTenant("tenant-1", "Contoso") };

        var ctx = Substitute.For<IPluginContext>();
        ctx.Logger.Returns(NullLogger<PimPluginApprovalReasonTests>.Instance);
        ctx.Tenants.Returns(tenants);
        ctx.ReadyTenants.Returns(tenants);
        ctx.Notifier.Returns(notifier);
        ctx.GraphScope.Returns("https://graph.microsoft.com/.default");
        ctx.ArmScope.Returns("https://management.azure.com/.default");
        ctx.DataDir.Returns(_dataDir);
        ctx.GetHttpClient(Arg.Any<string>()).Returns(http);
        return ctx;
    }

    private static INotifier NewNotifier()
    {
        var notifier = Substitute.For<INotifier>();
        notifier.ShowAsync(Arg.Any<NotificationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DismissedResult());
        return notifier;
    }

    // Only the approver's approve/deny popups; the activation watcher's own
    // toasts are InformationRequests and never match.
    private static List<ChoiceRequest> ApprovalPopups(INotifier notifier)
        => notifier.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<ChoiceRequest>()
            .Where(r => r.Title.StartsWith("PIM approval", StringComparison.Ordinal))
            .ToList();

    // HandleNewApprovalAsync is fired with `_ = ...` from the poll and from the
    // menu row, so completion is out-of-band.
    private static Task Settle() => Task.Delay(300);

    private sealed class StubHttp : IPluginHttpClient
    {
        private readonly string _graphPendingJson;
        private readonly string _armPendingJson;
        private readonly string _subscriptionsJson;

        public StubHttp(string graphPendingJson, string armPendingJson, string subscriptionsJson)
        {
            _graphPendingJson = graphPendingJson;
            _armPendingJson = armPendingJson;
            _subscriptionsJson = subscriptionsJson;
        }

        public Task<HttpResponseMessage> SendAsync(
            string clientName, string scope, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Both PIM clients build relative URIs, so ToString() is the only
            // safe accessor here (AbsoluteUri throws on a relative Uri).
            var json = Reply(request.RequestUri?.ToString() ?? string.Empty);
            return Task.FromResult(new HttpResponseMessage(
                json is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                Content = new StringContent(json ?? "{}", Encoding.UTF8, "application/json"),
            });
        }

        private string? Reply(string url)
        {
            if (url.Contains("v1.0/me", StringComparison.Ordinal)) return """{ "id": "prin-1" }""";
            if (url.Contains("subscriptions?api-version", StringComparison.Ordinal)) return _subscriptionsJson;
            // ARM's request feed shares the resource name with Graph's, so the
            // provider segment has to be tested first.
            if (url.Contains("Microsoft.Authorization/roleAssignmentScheduleRequests", StringComparison.Ordinal))
                return _armPendingJson;
            if (url.Contains("roleAssignmentScheduleRequests", StringComparison.Ordinal)) return _graphPendingJson;
            return EmptyJson;
        }
    }
}
