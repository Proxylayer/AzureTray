using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Arm;
using AzureTray.Plugin.PIM.Policies;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// ARM policy reads. Two deliberate design points are pinned here: the caps come
// off properties.effectiveRules on the assignments listing itself (no follow-up
// GET per policy), and rules are matched on id + ruleType only — ARM and Graph
// disagree on the casing of target values, so target is never consulted.
public sealed class ArmPimClientPolicyTests
{
    private const string ExpirationRuleType = "RoleManagementPolicyExpirationRule";
    private const string ApprovalRuleType = "RoleManagementPolicyApprovalRule";

    private const string SubScope = "/subscriptions/11111111-1111-1111-1111-111111111111";
    private const string ReaderRoleId =
        "/subscriptions/11111111-1111-1111-1111-111111111111/providers/Microsoft.Authorization/roleDefinitions/acdd72a7-3385-48ef-bd42-f606fba81ae7";

    // ---- the inline-rules optimisation ------------------------------------

    [Fact]
    public async Task GetRolePoliciesAsync_ReadsCapsInline_WithoutASecondPolicyGet()
    {
        var http = new RecordingPluginHttp(_ => Json(Page(AssignmentJson("PT2H"))));
        var client = new ArmPimClient(NewContext(http), "tenant-1");

        var policies = await client.GetRolePoliciesAsync(new[] { SubScope }, CancellationToken.None);

        // Exactly one request, and specifically not a per-policy follow-up.
        var url = Assert.Single(http.Urls);
        Assert.Contains(
            "subscriptions/11111111-1111-1111-1111-111111111111/providers/Microsoft.Authorization/roleManagementPolicyAssignments",
            url,
            StringComparison.Ordinal);
        Assert.Contains("api-version=2020-10-01", url, StringComparison.Ordinal);
        Assert.DoesNotContain(http.Urls, u =>
            u.Contains("/roleManagementPolicies/", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(
            TimeSpan.FromHours(2),
            policies[ArmRolePolicyKey.For(SubScope, ReaderRoleId)].MaxActivationDuration);
    }

    // ---- rule matching ----------------------------------------------------

    [Fact]
    public async Task GetRolePoliciesAsync_IgnoresAdminExpirationRules_EvenWhenTheyCarryDurations()
    {
        var policies = await ReadAsync(Page($$"""
            {
              "id": "{{SubScope}}/providers/Microsoft.Authorization/roleManagementPolicyAssignments/assign-1",
              "name": "assign-1",
              "properties": {
                "policyId": "{{SubScope}}/providers/Microsoft.Authorization/roleManagementPolicies/pol-1",
                "roleDefinitionId": "{{ReaderRoleId}}",
                "effectiveRules": [
                  {
                    "id": "Expiration_Admin_Eligibility",
                    "ruleType": "{{ExpirationRuleType}}",
                    "maximumDuration": "P365D",
                    "isExpirationRequired": false,
                    "target": { "caller": "Admin", "operations": [ "All" ], "level": "Eligibility" }
                  },
                  {
                    "id": "Expiration_Admin_Assignment",
                    "ruleType": "{{ExpirationRuleType}}",
                    "maximumDuration": "P30D",
                    "isExpirationRequired": true,
                    "target": { "caller": "Admin", "operations": [ "All" ], "level": "Assignment" }
                  }
                ]
              }
            }
            """));

        var policy = Assert.Single(policies).Value;
        Assert.Null(policy.MaxActivationDuration);
    }

    // ARM capitalises the operations values ("All") where Graph lower-cases them
    // ("all"). Matching on id + ruleType means neither spelling can break the
    // read; if anything ever starts matching on target, this fails.
    [Fact]
    public async Task GetRolePoliciesAsync_ArmCapitalisationOfTargetOperations_DoesNotBreakMatching()
    {
        var capitalised = await ReadAsync(Page(AssignmentJson("PT2H", operations: "\"All\"")));
        var lowerCased = await ReadAsync(Page(AssignmentJson("PT2H", operations: "\"all\"")));

        Assert.Equal(TimeSpan.FromHours(2), Assert.Single(capitalised).Value.MaxActivationDuration);
        Assert.Equal(TimeSpan.FromHours(2), Assert.Single(lowerCased).Value.MaxActivationDuration);
    }

    [Fact]
    public async Task GetRolePoliciesAsync_IgnoresAnEndUserRuleOfTheWrongRuleType()
    {
        var policies = await ReadAsync(Page($$"""
            {
              "id": "{{SubScope}}/providers/Microsoft.Authorization/roleManagementPolicyAssignments/assign-1",
              "name": "assign-1",
              "properties": {
                "roleDefinitionId": "{{ReaderRoleId}}",
                "effectiveRules": [
                  {
                    "id": "Expiration_EndUser_Assignment",
                    "ruleType": "{{ApprovalRuleType}}",
                    "maximumDuration": "PT2H"
                  }
                ]
              }
            }
            """));

        Assert.Null(Assert.Single(policies).Value.MaxActivationDuration);
    }

    [Fact]
    public async Task GetRolePoliciesAsync_ReadsApprovalAndCapTogether()
    {
        var policies = await ReadAsync(Page($$"""
            {
              "id": "{{SubScope}}/providers/Microsoft.Authorization/roleManagementPolicyAssignments/assign-1",
              "name": "assign-1",
              "properties": {
                "roleDefinitionId": "{{ReaderRoleId}}",
                "effectiveRules": [
                  {
                    "id": "Approval_EndUser_Assignment",
                    "ruleType": "{{ApprovalRuleType}}",
                    "setting": { "isApprovalRequired": true }
                  },
                  {
                    "id": "Expiration_EndUser_Assignment",
                    "ruleType": "{{ExpirationRuleType}}",
                    "maximumDuration": "PT30M"
                  }
                ]
              }
            }
            """));

        var policy = Assert.Single(policies).Value;
        Assert.True(policy.ApprovalRequired);
        Assert.Equal(TimeSpan.FromMinutes(30), policy.MaxActivationDuration);
    }

    [Fact]
    public async Task GetRolePoliciesAsync_AssignmentWithoutEffectiveRules_IsOmitted()
    {
        var policies = await ReadAsync(Page($$"""
            {
              "id": "{{SubScope}}/providers/Microsoft.Authorization/roleManagementPolicyAssignments/assign-1",
              "name": "assign-1",
              "properties": { "roleDefinitionId": "{{ReaderRoleId}}" }
            }
            """));

        Assert.Empty(policies);
    }

    // ---- keying -----------------------------------------------------------

    // ARM hands back the role definition id as a full resource path and is not
    // consistent about its casing, so the join key normalises both halves.
    [Fact]
    public async Task GetRolePoliciesAsync_NormalisesTheCasingOfTheFullResourceIdJoinKey()
    {
        var http = new RecordingPluginHttp(_ => Json(Page($$"""
            {
              "id": "assign-1",
              "name": "assign-1",
              "properties": {
                "roleDefinitionId": "{{ReaderRoleId.ToUpperInvariant()}}",
                "effectiveRules": [
                  { "id": "Expiration_EndUser_Assignment", "ruleType": "{{ExpirationRuleType}}", "maximumDuration": "PT2H" }
                ]
              }
            }
            """)));
        var client = new ArmPimClient(NewContext(http), "tenant-1");

        var policies = await client.GetRolePoliciesAsync(
            new[] { SubScope.ToUpperInvariant() }, CancellationToken.None);

        Assert.Equal(
            TimeSpan.FromHours(2),
            policies[ArmRolePolicyKey.For(SubScope, ReaderRoleId)].MaxActivationDuration);
    }

    // The same role can carry a different policy at a different scope, so the
    // scope is half the key — one request per scope, results kept apart.
    [Fact]
    public async Task GetRolePoliciesAsync_QueriesEachScope_AndKeepsResultsPerScope()
    {
        const string RgScope = SubScope + "/resourceGroups/rg-a";

        var http = new RecordingPluginHttp(url => Json(Page(
            url.Contains("resourceGroups/rg-a", StringComparison.OrdinalIgnoreCase)
                ? AssignmentJson("PT30M")
                : AssignmentJson("PT8H"))));
        var client = new ArmPimClient(NewContext(http), "tenant-1");

        var policies = await client.GetRolePoliciesAsync(
            new[] { SubScope, RgScope }, CancellationToken.None);

        Assert.Equal(2, http.Urls.Count);
        Assert.Equal(2, policies.Count);
        Assert.Equal(
            TimeSpan.FromHours(8),
            policies[ArmRolePolicyKey.For(SubScope, ReaderRoleId)].MaxActivationDuration);
        Assert.Equal(
            TimeSpan.FromMinutes(30),
            policies[ArmRolePolicyKey.For(RgScope, ReaderRoleId)].MaxActivationDuration);
    }

    [Fact]
    public async Task GetRolePoliciesAsync_NoScopes_MakesNoRequests()
    {
        var http = new RecordingPluginHttp(_ => Json(Page(AssignmentJson("PT2H"))));
        var client = new ArmPimClient(NewContext(http), "tenant-1");

        var policies = await client.GetRolePoliciesAsync(Array.Empty<string>(), CancellationToken.None);

        Assert.Empty(http.Urls);
        Assert.Empty(policies);
    }

    [Fact]
    public async Task GetRolePoliciesAsync_FollowsNextLink()
    {
        const string SecondRoleId = SubScope +
            "/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c";

        var http = new RecordingPluginHttp(url => url.Contains("$skiptoken", StringComparison.Ordinal)
            ? Json(Page($$"""
                {
                  "id": "assign-2",
                  "name": "assign-2",
                  "properties": {
                    "roleDefinitionId": "{{SecondRoleId}}",
                    "effectiveRules": [
                      { "id": "Expiration_EndUser_Assignment", "ruleType": "{{ExpirationRuleType}}", "maximumDuration": "PT1H" }
                    ]
                  }
                }
                """))
            : Json($$"""
                {
                  "value": [ {{AssignmentJson("PT2H")}} ],
                  "nextLink": "https://management.azure.com{{SubScope}}/providers/Microsoft.Authorization/roleManagementPolicyAssignments?api-version=2020-10-01&$skiptoken=page2"
                }
                """));
        var client = new ArmPimClient(NewContext(http), "tenant-1");

        var policies = await client.GetRolePoliciesAsync(new[] { SubScope }, CancellationToken.None);

        Assert.Equal(2, http.Urls.Count);
        Assert.Equal(2, policies.Count);
        Assert.Equal(
            TimeSpan.FromHours(2),
            policies[ArmRolePolicyKey.For(SubScope, ReaderRoleId)].MaxActivationDuration);
        Assert.Equal(
            TimeSpan.FromHours(1),
            policies[ArmRolePolicyKey.For(SubScope, SecondRoleId)].MaxActivationDuration);
    }

    // ---- the key itself ---------------------------------------------------

    [Fact]
    public void ArmRolePolicyKey_IgnoresCasingWhitespaceAndTrailingSlash()
    {
        var canonical = ArmRolePolicyKey.For(SubScope, ReaderRoleId);

        Assert.Equal(canonical, ArmRolePolicyKey.For(SubScope.ToUpperInvariant(), ReaderRoleId.ToUpperInvariant()));
        Assert.Equal(canonical, ArmRolePolicyKey.For(SubScope + "/", ReaderRoleId + "/"));
        Assert.Equal(canonical, ArmRolePolicyKey.For("  " + SubScope + "  ", "  " + ReaderRoleId + "  "));
        Assert.Equal(canonical.GetHashCode(), ArmRolePolicyKey.For(SubScope + "/", ReaderRoleId).GetHashCode());
    }

    [Fact]
    public void ArmRolePolicyKey_DistinguishesScopes()
    {
        Assert.NotEqual(
            ArmRolePolicyKey.For(SubScope, ReaderRoleId),
            ArmRolePolicyKey.For(SubScope + "/resourceGroups/rg-a", ReaderRoleId));
    }

    [Fact]
    public void ArmRolePolicyKey_BlankPartsNormaliseToEmpty()
    {
        Assert.Equal(ArmRolePolicyKey.For(null, null), ArmRolePolicyKey.For("", "   "));
    }

    // ---- harness ----------------------------------------------------------

    private static string Page(string assignmentJson) => $$"""{ "value": [ {{assignmentJson}} ] }""";

    // A realistic Expiration_EndUser_Assignment assignment, including the target
    // block the reader must ignore.
    private static string AssignmentJson(string maximumDuration, string operations = "\"All\"") => $$"""
        {
          "id": "{{SubScope}}/providers/Microsoft.Authorization/roleManagementPolicyAssignments/assign-1",
          "name": "assign-1",
          "properties": {
            "policyId": "{{SubScope}}/providers/Microsoft.Authorization/roleManagementPolicies/pol-1",
            "roleDefinitionId": "{{ReaderRoleId}}",
            "effectiveRules": [
              {
                "id": "Expiration_EndUser_Assignment",
                "ruleType": "{{ExpirationRuleType}}",
                "maximumDuration": "{{maximumDuration}}",
                "isExpirationRequired": true,
                "target": {
                  "caller": "EndUser",
                  "operations": [ {{operations}} ],
                  "level": "Assignment",
                  "targetObjects": null,
                  "inheritableSettings": null,
                  "enforcedSettings": null
                }
              }
            ]
          }
        }
        """;

    private static async Task<IReadOnlyDictionary<ArmRolePolicyKey, RolePolicy>> ReadAsync(
        string responseJson)
    {
        var http = new RecordingPluginHttp(_ => Json(responseJson));
        var client = new ArmPimClient(NewContext(http), "tenant-1");
        return await client.GetRolePoliciesAsync(new[] { SubScope }, CancellationToken.None);
    }

    private static IPluginContext NewContext(IPluginHttpClient http)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.GetHttpClient(Arg.Any<string>()).Returns(http);
        ctx.Logger.Returns(NullLogger<ArmPimClientPolicyTests>.Instance);
        ctx.ArmScope.Returns("https://management.azure.com/.default");
        ctx.GraphScope.Returns("https://graph.microsoft.com/.default");
        ctx.Tenants.Returns(new List<PluginTenant>());
        return ctx;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    // Records every request URL and replies from a single function of that URL.
    // Requests are serialised through a lock because the policy fan-out runs two
    // scopes at a time.
    private sealed class RecordingPluginHttp : IPluginHttpClient
    {
        private readonly Func<string, HttpResponseMessage> _reply;

        public RecordingPluginHttp(Func<string, HttpResponseMessage> reply) { _reply = reply; }

        public List<string> Urls { get; } = new();

        public Task<HttpResponseMessage> SendAsync(
            string clientName, string scope, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = Uri.UnescapeDataString(request.RequestUri?.ToString() ?? string.Empty);
            lock (Urls) { Urls.Add(url); }
            return Task.FromResult(_reply(url));
        }
    }
}
