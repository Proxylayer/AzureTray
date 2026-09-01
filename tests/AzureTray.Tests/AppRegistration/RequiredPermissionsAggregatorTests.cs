using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray.AppRegistration;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugins;
using Xunit;
using static AzureTray.Tests.AppRegistration.AppRegistrationTestFixtures;

namespace AzureTray.Tests.AppRegistration;

// Covers the validity filter every Graph-facing entry point runs first.
// It is deliberately load-bearing in two opposite directions: a malformed
// declaration must never reach a Graph request body (one non-GUID scope id
// makes Graph reject the whole PATCH, taking the host's and every other
// plugin's scopes with it), and it must never be read as a retraction
// either (see the EnsureAsync protection tests in
// AppRegistrationPermissionsTests). Nothing exercised this directly before,
// which is how the placeholder scope ids in the fixtures around it managed
// to go stale unnoticed.
public sealed class RequiredPermissionsAggregatorTests
{
    [Theory]
    [InlineData("11111111-1111-4111-8111-111111111111")]
    [InlineData("{11111111-1111-4111-8111-111111111111}")]
    public void HasValidScopeId_True_ForGuidScopeId(string scopeId)
        => Assert.True(RequiredPermissionsAggregator.HasValidScopeId(GraphRequirement("User.Read", scopeId)));

    [Theory]
    [InlineData("User.Read")]                 // the scope name in the id slot - the real-world mistake
    [InlineData("Application.Read.All")]
    [InlineData("id-user-read")]              // placeholder id, as stale fixtures used to carry
    [InlineData("")]
    [InlineData("   ")]
    public void HasValidScopeId_False_ForAnythingThatIsNotAGuid(string scopeId)
        => Assert.False(RequiredPermissionsAggregator.HasValidScopeId(GraphRequirement("User.Read", scopeId)));

    [Fact]
    public void KeepValid_ReturnsInputUntouched_WhenEveryScopeIdIsAGuid()
    {
        var required = new[]
        {
            GraphRequirement("User.Read", UserReadScopeId),
            GraphRequirement("RoleManagement.Read.Directory", RoleManagementReadDirectoryScopeId),
        };

        var kept = RequiredPermissionsAggregator.KeepValid(required, NullLogger.Instance);

        Assert.Equal(required, kept);
    }

    [Fact]
    public void KeepValid_DropsOnlyTheMalformedRequirement()
    {
        var good = GraphRequirement("User.Read", UserReadScopeId);
        var bad = GraphRequirement("RoleManagement.Read.Directory", "RoleManagement.Read.Directory");

        var kept = RequiredPermissionsAggregator.KeepValid(new[] { good, bad }, NullLogger.Instance);

        // The point of filtering rather than throwing: the good scope still
        // gets provisioned instead of one bad plugin declaration aborting
        // the whole request.
        Assert.Equal(new[] { good }, kept);
    }

    [Fact]
    public void KeepValid_ReturnsEmpty_WhenNothingIsWellFormed()
    {
        var kept = RequiredPermissionsAggregator.KeepValid(
            new[] { GraphRequirement("User.Read", "User.Read") }, NullLogger.Instance);

        Assert.Empty(kept);
    }

    [Fact]
    public void Aggregate_AcceptsGuidDeclarations_AndReportsTheRestAsRejected()
    {
        var good = GraphRequirement("Files.Read.All", UserReadScopeId);
        var bad = GraphRequirement("RoleManagement.Read.Directory", "RoleManagement.Read.Directory");
        var loader = LoaderWith("plugin-x", good, bad);

        var result = RequiredPermissionsAggregator.Aggregate(loader, NullLogger.Instance);

        Assert.Contains(good, result.Required);
        Assert.DoesNotContain(bad, result.Required);
        // Every host scope is well-formed, so the plugin's is the only reject.
        var rejected = Assert.Single(result.Rejected);
        Assert.Equal("plugin-x", rejected.Source);
        Assert.Equal("RoleManagement.Read.Directory", rejected.ScopeName);
        Assert.All(result.Required, r => Assert.True(RequiredPermissionsAggregator.HasValidScopeId(r)));
    }

    [Fact]
    public void Aggregate_RejectionNote_NamesThePluginAndSaysNothingWasRemoved()
    {
        var loader = LoaderWith("plugin-x", GraphRequirement("RoleManagement.Read.Directory", "RoleManagement.Read.Directory"));

        var note = RequiredPermissionsAggregator.Aggregate(loader, NullLogger.Instance).RejectionNote;

        Assert.Contains("RoleManagement.Read.Directory", note);
        Assert.Contains("plugin-x", note);
        Assert.Contains("Existing consent was left untouched.", note);
    }

    [Fact]
    public void Aggregate_RejectionNote_IsEmpty_WhenEveryDeclarationIsWellFormed()
    {
        var loader = LoaderWith("plugin-x", GraphRequirement("Files.Read.All", UserReadScopeId));

        var result = RequiredPermissionsAggregator.Aggregate(loader, NullLogger.Instance);

        Assert.Empty(result.Rejected);
        Assert.Equal(string.Empty, result.RejectionNote);
    }

    private static IPluginLoader LoaderWith(string pluginId, params PluginPermissionRequirement[] permissions)
    {
        var plugin = Substitute.For<ITrayPlugin>();
        plugin.Id.Returns(pluginId);
        plugin.RequiredPermissions.Returns(permissions);

        var loader = Substitute.For<IPluginLoader>();
        loader.LoadedPlugins.Returns(new[] { new LoadedPlugin(plugin, $@"C:\plugins\{pluginId}.dll", SignatureVerdict.NotSigned) });
        return loader;
    }
}
