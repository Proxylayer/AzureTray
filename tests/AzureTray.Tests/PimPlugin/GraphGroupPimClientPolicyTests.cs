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
using AzureTray.Plugin.PIM.Groups;
using AzureTray.Plugin.PIM.Policies;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// GetGroupPoliciesAsync over a stub HTTP handler. PIM for Groups policies are
// NOT under the privilegedAccess root — they are the same tenant-level
// roleManagementPolicyAssignments collection the directory roles use,
// distinguished only by scopeType 'Group' and a scopeId that is the group's
// object id, so the query text is the whole contract and a wrong scopeType
// returns an empty set rather than an error.
//
// Two further things are pinned. First, roleDefinitionId is deliberately absent
// from the filter: one request must come back with BOTH of the group's policies
// (the assignment's roleDefinitionId is then the literal "member" / "owner"),
// because doubling the request count per group is the difference between a
// trickle and a throttle. Second, the same trap the directory-role policy tests
// guard — the activation cap comes from Expiration_EndUser_Assignment and from
// nothing else. The Admin_* expiration rules are days-scale caps on how long an
// admin may grant access; letting one through would offer a 365-day activation
// the service then rejects.
public sealed class GraphGroupPimClientPolicyTests
{
    private const string ExpirationType = "#microsoft.graph.unifiedRoleManagementPolicyExpirationRule";
    private const string ApprovalType = "#microsoft.graph.unifiedRoleManagementPolicyApprovalRule";

    [Fact]
    public async Task GetGroupPoliciesAsync_FiltersByGroupScope_AndExpandsThePolicysRules()
    {
        var http = new RecordingPluginHttp(_ => Json(EmptyPage));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        await client.GetGroupPoliciesAsync(new[] { "group-1" }, CancellationToken.None);

        var url = Assert.Single(http.Urls);
        Assert.StartsWith("v1.0/policies/roleManagementPolicyAssignments?", url, StringComparison.Ordinal);
        Assert.Contains("$filter=scopeId eq 'group-1' and scopeType eq 'Group'", url, StringComparison.Ordinal);
        Assert.Contains("$expand=policy($expand=rules)", url, StringComparison.Ordinal);
        // No roleDefinitionId in the filter: one request must answer for both
        // the member and the owner policy.
        Assert.DoesNotContain("roleDefinitionId", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetGroupPoliciesAsync_ReadsTheCapFromTheEndUserExpirationRule()
    {
        var policies = await ReadAsync(Page(Assignment("member", Rule("Expiration_EndUser_Assignment", "PT2H"))));

        var policy = Assert.Single(policies).Value;
        Assert.Equal(TimeSpan.FromHours(2), policy.MaxActivationDuration);
    }

    // The regression guard, group-side: a policy whose only expiration rules are
    // the days-scale admin ones has NO readable self-activation cap.
    [Fact]
    public async Task GetGroupPoliciesAsync_IgnoresAdminExpirationRules_EvenWhenTheyCarryDurations()
    {
        var policies = await ReadAsync(Page(Assignment(
            "member",
            Rule("Expiration_Admin_Eligibility", "P365D"),
            Rule("Expiration_Admin_Assignment", "P30D"))));

        var policy = Assert.Single(policies).Value;
        Assert.Null(policy.MaxActivationDuration);
    }

    [Fact]
    public async Task GetGroupPoliciesAsync_PrefersTheEndUserRule_OverAdminRulesInTheSamePolicy()
    {
        var policies = await ReadAsync(Page(Assignment(
            "member",
            Rule("Expiration_Admin_Eligibility", "P365D"),
            Rule("Expiration_EndUser_Assignment", "PT4H"),
            Rule("Expiration_Admin_Assignment", "P30D"))));

        var policy = Assert.Single(policies).Value;
        Assert.Equal(TimeSpan.FromHours(4), policy.MaxActivationDuration);
    }

    // The reason roleDefinitionId is left out of the filter: both of a group's
    // policies arrive together, keyed apart by the access id.
    [Fact]
    public async Task GetGroupPoliciesAsync_OneResponse_YieldsBothTheMemberAndTheOwnerPolicy()
    {
        var policies = await ReadAsync(
            Page(
                Assignment("member", Rule("Expiration_EndUser_Assignment", "PT8H")),
                Assignment("owner", Rule("Expiration_EndUser_Assignment", "PT1H"))));

        Assert.Equal(2, policies.Count);
        Assert.Equal(
            TimeSpan.FromHours(8),
            policies[GroupRolePolicyKey.For("group-1", "member")].MaxActivationDuration);
        Assert.Equal(
            TimeSpan.FromHours(1),
            policies[GroupRolePolicyKey.For("group-1", "owner")].MaxActivationDuration);
    }

    // Object ids and access ids both come back with inconsistent casing, so the
    // key normalizes both ends — otherwise the join back to an eligible row
    // misses and every group row loses its cap.
    [Fact]
    public async Task GetGroupPoliciesAsync_KeysTheGroupAndAccessIdCaseInsensitively()
    {
        var http = new RecordingPluginHttp(_ => Json(
            Page(Assignment("Member", Rule("Expiration_EndUser_Assignment", "PT2H")))));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        var policies = await client.GetGroupPoliciesAsync(new[] { "GROUP-1" }, CancellationToken.None);

        Assert.True(policies.ContainsKey(GroupRolePolicyKey.For("group-1", "member")));
        Assert.True(policies.ContainsKey(GroupRolePolicyKey.For("Group-1", "MEMBER")));
    }

    [Fact]
    public async Task GetGroupPoliciesAsync_ReadsApprovalAndCapTogether()
    {
        var policies = await ReadAsync(Page(Assignment(
            "member",
            $$"""{ "@odata.type": "{{ApprovalType}}", "id": "Approval_EndUser_Assignment", "setting": { "isApprovalRequired": true } }""",
            Rule("Expiration_EndUser_Assignment", "PT30M"))));

        var policy = Assert.Single(policies).Value;
        Assert.True(policy.ApprovalRequired);
        Assert.Equal(TimeSpan.FromMinutes(30), policy.MaxActivationDuration);
    }

    // An assignment whose policy was not expanded carries no rules at all. That
    // is "unknown", so it must be absent from the dictionary rather than present
    // with null members — the caller distinguishes the two.
    [Fact]
    public async Task GetGroupPoliciesAsync_AssignmentWithoutAnExpandedPolicy_IsOmitted()
    {
        var policies = await ReadAsync("""
            { "value": [ {
                "id": "assign-1",
                "policyId": "pol-1",
                "roleDefinitionId": "member",
                "scopeId": "group-1",
                "scopeType": "Group"
            } ] }
            """);

        Assert.Empty(policies);
    }

    [Fact]
    public async Task GetGroupPoliciesAsync_NoGroups_MakesNoRequests()
    {
        var http = new RecordingPluginHttp(_ => Json(EmptyPage));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        var policies = await client.GetGroupPoliciesAsync(Array.Empty<string>(), CancellationToken.None);

        Assert.Empty(policies);
        Assert.Empty(http.Urls);
    }

    // There is no tenant-wide bulk form, so the fan-out is one request per
    // DISTINCT group — a group listed twice (member and owner rows) must not be
    // asked about twice.
    [Fact]
    public async Task GetGroupPoliciesAsync_AsksOncePerDistinctGroup()
    {
        var http = new RecordingPluginHttp(_ => Json(EmptyPage));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        await client.GetGroupPoliciesAsync(
            new[] { "group-1", "GROUP-1", "group-2", " group-2 " }, CancellationToken.None);

        Assert.Equal(2, http.Urls.Count);
        Assert.Contains(http.Urls, u => u.Contains("scopeId eq 'group-1'", StringComparison.Ordinal));
        Assert.Contains(http.Urls, u => u.Contains("scopeId eq 'group-2'", StringComparison.Ordinal));
    }

    // A 403 (the PIM for Groups policy scope was never consented) must surface
    // for the watcher to degrade on, not read as "no caps anywhere".
    [Fact]
    public async Task GetGroupPoliciesAsync_Forbidden_Throws()
    {
        var http = new RecordingPluginHttp(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                """{ "error": { "code": "Authorization_RequestDenied" } }""",
                Encoding.UTF8,
                "application/json"),
        });
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetGroupPoliciesAsync(new[] { "group-1" }, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    // ---- harness ----------------------------------------------------------

    private const string EmptyPage = """{ "value": [] }""";

    // Group-scoped assignments expand `rules`, not the `effectiveRules` the
    // directory-role reads use — the two are read through the same accessor.
    private static string Assignment(string accessId, params string[] rules) => $$"""
        {
          "id": "assign-{{accessId}}",
          "policyId": "pol-{{accessId}}",
          "roleDefinitionId": "{{accessId}}",
          "scopeId": "group-1",
          "scopeType": "Group",
          "policy": { "id": "pol-{{accessId}}", "rules": [ {{string.Join(", ", rules)}} ] }
        }
        """;

    private static string Rule(string id, string maximumDuration) => $$"""
        { "@odata.type": "{{ExpirationType}}", "id": "{{id}}", "maximumDuration": "{{maximumDuration}}" }
        """;

    private static string Page(params string[] assignments)
        => $$"""{ "value": [ {{string.Join(", ", assignments)}} ] }""";

    private static async Task<IReadOnlyDictionary<GroupRolePolicyKey, RolePolicy>> ReadAsync(string responseJson)
    {
        var http = new RecordingPluginHttp(_ => Json(responseJson));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");
        return await client.GetGroupPoliciesAsync(new[] { "group-1" }, CancellationToken.None);
    }

    private static IPluginContext NewContext(IPluginHttpClient http)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.GetHttpClient(Arg.Any<string>()).Returns(http);
        ctx.Logger.Returns(NullLogger<GraphGroupPimClientPolicyTests>.Instance);
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
