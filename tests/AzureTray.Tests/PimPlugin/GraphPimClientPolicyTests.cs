using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Graph;
using AzureTray.Plugin.PIM.Policies;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// GetRolePoliciesAsync over a stub HTTP handler. Two things are being pinned:
// the request must carry the $filter and the plain nested $expand that make the
// caps come back at all — a nested $select naming derived-type properties is a
// 400 at OData parse time, which is what shipped — and the response reader must
// take the activation cap from Expiration_EndUser_Assignment and from nothing
// else. The Admin_* expiration rules are days-scale caps on how long an admin
// may grant eligibility; letting one of those through as a self-activation cap
// would offer the user a 365-day activation that the service then rejects.
public sealed class GraphPimClientPolicyTests
{
    private const string ExpirationType = "#microsoft.graph.unifiedRoleManagementPolicyExpirationRule";
    private const string ApprovalType = "#microsoft.graph.unifiedRoleManagementPolicyApprovalRule";

    // Graph names roles by bare GUID in policy assignments, so the key joins
    // straight onto an eligible role's RoleDefinitionId.
    private const string GlobalReaderId = "f2ef992c-3afb-46b9-b7cf-a126ee74c451";

    // The nested $select this used to assert was rejected by Graph on every
    // call: effectiveRules is a collection of the base rule type, and
    // maximumDuration / isExpirationRequired / setting exist only on derived
    // types, so the query died at parse time with a 400. effectiveRules is now
    // expanded whole — no nested $select at all.
    [Fact]
    public async Task GetRolePoliciesAsync_RequestsDirectoryScopedAssignments_ExpandingEffectiveRulesWithoutANestedSelect()
    {
        var http = new RecordingPluginHttp(_ => Json(EmptyPage));
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        await client.GetRolePoliciesAsync(CancellationToken.None);

        var url = http.Urls[0];
        Assert.StartsWith("v1.0/policies/roleManagementPolicyAssignments?", url, StringComparison.Ordinal);
        Assert.Contains("$filter=scopeId eq '/' and scopeType eq 'Directory'", url, StringComparison.Ordinal);
        Assert.Contains("$expand=policy($expand=effectiveRules)", url, StringComparison.Ordinal);
        Assert.DoesNotContain("$select", url, StringComparison.Ordinal);
    }

    // 'Directory' is unverifiable by inspection — a wrong scopeType returns an
    // empty set rather than an error — so an empty result is treated as "maybe
    // the wrong scopeType" and retried once with the value Microsoft's own v1.0
    // Entra-role example uses.
    [Fact]
    public async Task GetRolePoliciesAsync_ZeroAssignments_RetriesOnceWithTheDirectoryRoleScopeType()
    {
        var http = new RecordingPluginHttp(_ => Json(EmptyPage));
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        await client.GetRolePoliciesAsync(CancellationToken.None);

        Assert.Equal(2, http.Urls.Count);
        Assert.Contains("scopeType eq 'Directory'", http.Urls[0], StringComparison.Ordinal);
        Assert.Contains("scopeType eq 'DirectoryRole'", http.Urls[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRolePoliciesAsync_ReadsTheCapFromTheEndUserExpirationRule()
    {
        var policies = await ReadAsync(Page($$"""
            {
              "id": "assign-1",
              "policyId": "pol-1",
              "roleDefinitionId": "{{GlobalReaderId}}",
              "scopeId": "/",
              "scopeType": "Directory",
              "policy": {
                "id": "pol-1",
                "effectiveRules": [
                  { "@odata.type": "{{ExpirationType}}", "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT2H", "isExpirationRequired": true }
                ]
              }
            }
            """));

        var policy = Assert.Single(policies).Value;
        Assert.Equal(TimeSpan.FromHours(2), policy.MaxActivationDuration);
    }

    // The regression guard. A policy whose only expiration rules are the
    // days-scale admin ones has NO readable self-activation cap; leaking P365D
    // or P30D through here would put "365d" in the activation prompt.
    [Fact]
    public async Task GetRolePoliciesAsync_IgnoresAdminExpirationRules_EvenWhenTheyCarryDurations()
    {
        var policies = await ReadAsync(Page($$"""
            {
              "id": "assign-1",
              "policyId": "pol-1",
              "roleDefinitionId": "{{GlobalReaderId}}",
              "scopeId": "/",
              "scopeType": "Directory",
              "policy": {
                "id": "pol-1",
                "effectiveRules": [
                  { "@odata.type": "{{ExpirationType}}", "id": "Expiration_Admin_Eligibility", "maximumDuration": "P365D", "isExpirationRequired": false },
                  { "@odata.type": "{{ExpirationType}}", "id": "Expiration_Admin_Assignment", "maximumDuration": "P30D", "isExpirationRequired": true }
                ]
              }
            }
            """));

        var policy = Assert.Single(policies).Value;
        Assert.Null(policy.MaxActivationDuration);
    }

    // Same rules present alongside the real one: the end-user rule wins, and the
    // admin durations are not consulted even as a tie-break.
    [Fact]
    public async Task GetRolePoliciesAsync_PrefersTheEndUserRule_OverAdminRulesInTheSamePolicy()
    {
        var policies = await ReadAsync(Page($$"""
            {
              "id": "assign-1",
              "policyId": "pol-1",
              "roleDefinitionId": "{{GlobalReaderId}}",
              "scopeId": "/",
              "scopeType": "Directory",
              "policy": {
                "id": "pol-1",
                "effectiveRules": [
                  { "@odata.type": "{{ExpirationType}}", "id": "Expiration_Admin_Eligibility", "maximumDuration": "P365D" },
                  { "@odata.type": "{{ExpirationType}}", "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT4H" },
                  { "@odata.type": "{{ExpirationType}}", "id": "Expiration_Admin_Assignment", "maximumDuration": "P30D" }
                ]
              }
            }
            """));

        var policy = Assert.Single(policies).Value;
        Assert.Equal(TimeSpan.FromHours(4), policy.MaxActivationDuration);
    }

    // The rule id is right but the type says it is an approval rule — a payload
    // shape we should not trust to be a duration source.
    [Fact]
    public async Task GetRolePoliciesAsync_IgnoresAnEndUserRuleOfTheWrongODataType()
    {
        var policies = await ReadAsync(Page($$"""
            {
              "id": "assign-1",
              "policyId": "pol-1",
              "roleDefinitionId": "{{GlobalReaderId}}",
              "scopeId": "/",
              "scopeType": "Directory",
              "policy": {
                "id": "pol-1",
                "effectiveRules": [
                  { "@odata.type": "{{ApprovalType}}", "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT2H" }
                ]
              }
            }
            """));

        var policy = Assert.Single(policies).Value;
        Assert.Null(policy.MaxActivationDuration);
    }

    [Fact]
    public async Task GetRolePoliciesAsync_NoExpirationRule_LeavesTheCapUnknownButStillReadsApproval()
    {
        var policies = await ReadAsync(Page($$"""
            {
              "id": "assign-1",
              "policyId": "pol-1",
              "roleDefinitionId": "{{GlobalReaderId}}",
              "scopeId": "/",
              "scopeType": "Directory",
              "policy": {
                "id": "pol-1",
                "effectiveRules": [
                  { "@odata.type": "{{ApprovalType}}", "id": "Approval_EndUser_Assignment", "setting": { "isApprovalRequired": true } }
                ]
              }
            }
            """));

        var policy = Assert.Single(policies).Value;
        Assert.Null(policy.MaxActivationDuration);
        Assert.True(policy.ApprovalRequired);
    }

    [Fact]
    public async Task GetRolePoliciesAsync_ReadsApprovalAndCapTogether()
    {
        var policies = await ReadAsync(Page($$"""
            {
              "id": "assign-1",
              "policyId": "pol-1",
              "roleDefinitionId": "{{GlobalReaderId}}",
              "scopeId": "/",
              "scopeType": "Directory",
              "policy": {
                "id": "pol-1",
                "effectiveRules": [
                  { "@odata.type": "{{ApprovalType}}", "id": "Approval_EndUser_Assignment", "setting": { "isApprovalRequired": false } },
                  { "@odata.type": "{{ExpirationType}}", "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT30M" }
                ]
              }
            }
            """));

        var policy = Assert.Single(policies).Value;
        Assert.False(policy.ApprovalRequired);
        Assert.Equal(TimeSpan.FromMinutes(30), policy.MaxActivationDuration);
    }

    // Keyed by the bare GUID as sent, and looked up case-insensitively: Graph's
    // casing of a GUID is not something to depend on.
    [Fact]
    public async Task GetRolePoliciesAsync_KeysByBareGuidRoleDefinitionId_CaseInsensitively()
    {
        var policies = await ReadAsync(Page($$"""
            {
              "id": "assign-1",
              "policyId": "pol-1",
              "roleDefinitionId": "{{GlobalReaderId}}",
              "scopeId": "/",
              "scopeType": "Directory",
              "policy": {
                "id": "pol-1",
                "effectiveRules": [
                  { "@odata.type": "{{ExpirationType}}", "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT8H" }
                ]
              }
            }
            """));

        Assert.True(policies.ContainsKey(GlobalReaderId));
        Assert.True(policies.ContainsKey(GlobalReaderId.ToUpperInvariant()));
        Assert.Equal(TimeSpan.FromHours(8), policies[GlobalReaderId.ToUpperInvariant()].MaxActivationDuration);
    }

    // An assignment whose policy was not expanded carries no rules at all. That
    // is "unknown", so it must be absent from the dictionary rather than present
    // with null members — the caller distinguishes the two.
    [Fact]
    public async Task GetRolePoliciesAsync_AssignmentWithoutAnExpandedPolicy_IsOmitted()
    {
        var policies = await ReadAsync(Page($$"""
            {
              "id": "assign-1",
              "policyId": "pol-1",
              "roleDefinitionId": "{{GlobalReaderId}}",
              "scopeId": "/",
              "scopeType": "Directory"
            }
            """));

        Assert.Empty(policies);
    }

    [Fact]
    public async Task GetRolePoliciesAsync_AssignmentWithoutARoleDefinitionId_IsSkipped()
    {
        var policies = await ReadAsync(Page($$"""
            {
              "id": "assign-1",
              "policyId": "pol-1",
              "roleDefinitionId": null,
              "scopeId": "/",
              "scopeType": "Directory",
              "policy": {
                "id": "pol-1",
                "effectiveRules": [
                  { "@odata.type": "{{ExpirationType}}", "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT2H" }
                ]
              }
            }
            """));

        Assert.Empty(policies);
    }

    // A tenant with many roles pages, and a cap that only exists on page two
    // must not be lost.
    [Fact]
    public async Task GetRolePoliciesAsync_FollowsODataNextLink()
    {
        const string SecondRoleId = "194ae4cb-b126-40b2-bd5b-6091b380977d";

        var http = new RecordingPluginHttp(url => url.Contains("$skiptoken", StringComparison.Ordinal)
            ? Json($$"""
                { "value": [ {
                    "id": "assign-2",
                    "policyId": "pol-2",
                    "roleDefinitionId": "{{SecondRoleId}}",
                    "scopeId": "/",
                    "scopeType": "Directory",
                    "policy": { "id": "pol-2", "effectiveRules": [
                      { "@odata.type": "{{ExpirationType}}", "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT30M" }
                    ] }
                } ] }
                """)
            : Json($$"""
                {
                  "value": [ {
                    "id": "assign-1",
                    "policyId": "pol-1",
                    "roleDefinitionId": "{{GlobalReaderId}}",
                    "scopeId": "/",
                    "scopeType": "Directory",
                    "policy": { "id": "pol-1", "effectiveRules": [
                      { "@odata.type": "{{ExpirationType}}", "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT2H" }
                    ] }
                  } ],
                  "@odata.nextLink": "https://graph.microsoft.com/v1.0/policies/roleManagementPolicyAssignments?$skiptoken=page2"
                }
                """));

        var client = new GraphPimClient(NewContext(http), "tenant-1");
        var policies = await client.GetRolePoliciesAsync(CancellationToken.None);

        Assert.Equal(2, http.Urls.Count);
        Assert.Equal(2, policies.Count);
        Assert.Equal(TimeSpan.FromHours(2), policies[GlobalReaderId].MaxActivationDuration);
        Assert.Equal(TimeSpan.FromMinutes(30), policies[SecondRoleId].MaxActivationDuration);
    }

    // A 403 (the signed-in user holds none of the directory roles that permit
    // reading policies) must surface as an exception for the watcher to degrade
    // on, not as a silently empty result that reads like "no caps anywhere".
    [Fact]
    public async Task GetRolePoliciesAsync_Forbidden_Throws()
    {
        var http = new RecordingPluginHttp(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                """{ "error": { "code": "Authorization_RequestDenied" } }""",
                Encoding.UTF8,
                "application/json"),
        });
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetRolePoliciesAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    // ---- harness ----------------------------------------------------------

    private const string EmptyPage = """{ "value": [] }""";

    private static string Page(string assignmentJson) => $$"""{ "value": [ {{assignmentJson}} ] }""";

    private static async Task<IReadOnlyDictionary<string, RolePolicy>> ReadAsync(
        string responseJson)
    {
        var http = new RecordingPluginHttp(_ => Json(responseJson));
        var client = new GraphPimClient(NewContext(http), "tenant-1");
        return await client.GetRolePoliciesAsync(CancellationToken.None);
    }

    private static IPluginContext NewContext(IPluginHttpClient http)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.GetHttpClient(Arg.Any<string>()).Returns(http);
        ctx.Logger.Returns(NullLogger<GraphPimClientPolicyTests>.Instance);
        ctx.GraphScope.Returns("https://graph.microsoft.com/.default");
        ctx.ArmScope.Returns("https://management.azure.com/.default");
        ctx.Tenants.Returns(new List<PluginTenant>());
        return ctx;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    // Records every request URL (unescaped, so the $filter/$expand text can be
    // asserted as written) and replies from a single function of that URL.
    private sealed class RecordingPluginHttp : IPluginHttpClient
    {
        private readonly Func<string, HttpResponseMessage> _reply;

        public RecordingPluginHttp(Func<string, HttpResponseMessage> reply) { _reply = reply; }

        public List<string> Urls { get; } = new();

        public Task<HttpResponseMessage> SendAsync(
            string clientName, string scope, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The PIM clients build relative URIs, so ToString() is the only safe
            // accessor (AbsoluteUri throws on a relative Uri).
            var url = Uri.UnescapeDataString(request.RequestUri?.ToString() ?? string.Empty);
            Urls.Add(url);
            return Task.FromResult(_reply(url));
        }
    }
}
