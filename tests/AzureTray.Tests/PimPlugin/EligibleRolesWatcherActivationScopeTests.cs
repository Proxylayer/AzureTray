using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Arm;
using AzureTray.Plugin.PIM.Arm.Dto;
using AzureTray.Plugin.PIM.Dto;
using AzureTray.Plugin.PIM.Graph;
using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// What the watcher hands the provider clients when a row is clicked: the Entra
// row's own directory scope (not a hardcoded "/"), and — on the ARM side — the
// two pre-flight guards, which are deliberately not equivalent. A missing
// eligibility id is optional on ARM's contract, so it warns and proceeds; a
// missing scope has no URL to PUT to, so it still refuses outright.
public sealed class EligibleRolesWatcherActivationScopeTests
{
    private const string AuScope = "/administrativeUnits/au-1";
    private const string SubScope = "/subscriptions/sub-1";

    // ---- Entra scope pass-through -----------------------------------------

    [Fact]
    public async Task HandleActivationAsync_AdministrativeUnitScopedRole_ActivatesAtThatScope()
    {
        var graph = NewGraph();
        var watcher = NewWatcher(graph, NewArm(), PromptingNotifier());

        await watcher.HandleActivationAsync(EntraRole(AuScope), CancellationToken.None);

        await graph.Received(1).ActivateRoleAsync(
            "prin-1", "role-groups-admin", AuScope,
            TimeSpan.FromHours(1), "incident #42", Arg.Any<CancellationToken>());
    }

    // A row hydrated from a cache written before the scope was persisted has no
    // scope at all: that must still activate, directory-wide.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleActivationAsync_RoleWithNoDirectoryScope_ActivatesAtTheDirectory(string? scope)
    {
        var graph = NewGraph();
        var watcher = NewWatcher(graph, NewArm(), PromptingNotifier());

        await watcher.HandleActivationAsync(EntraRole(scope), CancellationToken.None);

        await graph.Received(1).ActivateRoleAsync(
            "prin-1", "role-groups-admin", "/",
            TimeSpan.FromHours(1), "incident #42", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleDeactivationAsync_AdministrativeUnitScopedRole_DeactivatesAtThatScope()
    {
        var graph = NewGraph();
        var watcher = NewWatcher(graph, NewArm(), ConfirmingNotifier());

        await watcher.HandleDeactivationAsync(EntraRole(AuScope), CancellationToken.None);

        await graph.Received(1).DeactivateRoleAsync(
            "prin-1", "role-groups-admin", AuScope, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleDeactivationAsync_RoleWithNoDirectoryScope_DeactivatesAtTheDirectory(string? scope)
    {
        var graph = NewGraph();
        var watcher = NewWatcher(graph, NewArm(), ConfirmingNotifier());

        await watcher.HandleDeactivationAsync(EntraRole(scope), CancellationToken.None);

        await graph.Received(1).DeactivateRoleAsync(
            "prin-1", "role-groups-admin", "/", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- ARM pre-flight guards --------------------------------------------

    // linkedRoleEligibilityScheduleId is optional on ARM's
    // roleAssignmentScheduleRequests contract, so a row that lost its eligibility
    // id (the collapse picked a Direct row that had none, or the cache predates
    // it) must not be left dead in the menu: warn, and let ARM decide.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleActivationAsync_ArmRoleWithNoEligibilityId_ActivatesAnywayAndWarns(string? eligibilityId)
    {
        var logger = new RecordingLogger();
        var arm = NewArm();
        var watcher = NewWatcher(NewGraph(), arm, PromptingNotifier(), logger);

        await watcher.HandleActivationAsync(ArmRole(SubScope, eligibilityId), CancellationToken.None);

        await arm.Received(1).ActivateRoleAsync(
            SubScope,
            "prin-1",
            "role-reader",
            Arg.Is<string?>(id => string.IsNullOrWhiteSpace(id)),
            TimeSpan.FromHours(1),
            "incident #42",
            Arg.Any<CancellationToken>());
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("no eligibility id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleActivationAsync_ArmRoleWithAnEligibilityId_PassesItThroughWithoutWarning()
    {
        var logger = new RecordingLogger();
        var arm = NewArm();
        var watcher = NewWatcher(NewGraph(), arm, PromptingNotifier(), logger);

        await watcher.HandleActivationAsync(ArmRole(SubScope, "elig-arm-1"), CancellationToken.None);

        await arm.Received(1).ActivateRoleAsync(
            SubScope, "prin-1", "role-reader", "elig-arm-1",
            TimeSpan.FromHours(1), "incident #42", Arg.Any<CancellationToken>());
        Assert.DoesNotContain(
            logger.Entries,
            e => e.Message.Contains("no eligibility id", StringComparison.Ordinal));
    }

    // The other guard, which must NOT have been relaxed: without a scope there is
    // no URL to PUT the activation to, so the click is refused and the user is
    // told — no ARM call at all.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleActivationAsync_ArmRoleWithNoScope_RefusesAndDoesNotCallArm(string? armScope)
    {
        var notifier = PromptingNotifier();
        var arm = NewArm();
        var watcher = NewWatcher(NewGraph(), arm, notifier);

        await watcher.HandleActivationAsync(ArmRole(armScope, "elig-arm-1"), CancellationToken.None);

        await arm.DidNotReceive().ActivateRoleAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Pinned to the guard's own toast, not the generic catch-all one: a
        // Details block would mean an exception got here instead.
        await notifier.Received(1).ShowAsync(
            Arg.Is<InformationRequest>(r =>
                r.Severity == NotificationSeverity.Error
                && r.Message == "Cannot activate — the role has no ARM scope to act on."
                && r.Details == null),
            Arg.Any<CancellationToken>());
    }

    // ---- builders ---------------------------------------------------------

    private static UnifiedEligibleRole EntraRole(string? directoryScopeId)
        => new(
            Source: PimSource.EntraId,
            RoleName: "Groups Administrator",
            RoleDefinitionId: "role-groups-admin",
            ScopeDisplay: EntraDirectoryScope.DisplayFor(directoryScopeId),
            ArmScope: null,
            EligibilityId: "elig-1",
            MaxActivationDuration: null,
            MemberType: "Direct",
            DirectoryScopeId: directoryScopeId);

    private static UnifiedEligibleRole ArmRole(string? armScope, string? eligibilityId)
        => new(
            Source: PimSource.AzureRbac,
            RoleName: "Reader",
            RoleDefinitionId: "role-reader",
            ScopeDisplay: "Dev sub",
            ArmScope: armScope,
            EligibilityId: eligibilityId,
            MaxActivationDuration: null,
            MemberType: "Direct",
            DirectoryScopeId: null);

    private static INotifier PromptingNotifier()
    {
        var notifier = Substitute.For<INotifier>();
        notifier.ShowAsync(Arg.Any<ChoiceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChoiceResult("1 hour", null));
        notifier.ShowAsync(Arg.Any<TextInputRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TextInputResult("incident #42"));
        return notifier;
    }

    private static INotifier ConfirmingNotifier()
    {
        var notifier = Substitute.For<INotifier>();
        notifier.ShowAsync(Arg.Any<YesNoRequest>(), Arg.Any<CancellationToken>())
            .Returns(new YesNoResult(true));
        return notifier;
    }

    private static IGraphPimClient NewGraph()
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetSignedInUserIdAsync(Arg.Any<CancellationToken>()).Returns("prin-1");
        graph.ListEligibleRolesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EntraEligibilitySchedule>());
        graph.ListActiveRoleAssignmentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EntraEligibilitySchedule>());
        graph.ActivateRoleAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(GraphRequest());
        graph.DeactivateRoleAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(GraphRequest());
        return graph;
    }

    private static IArmPimClient NewArm()
    {
        var arm = Substitute.For<IArmPimClient>();
        arm.ListSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ArmSubscription>());
        arm.ListEligibleRolesAsync(Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ArmEligibilitySchedule>());
        arm.ListActiveRoleAssignmentsAsync(Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ArmRoleAssignmentScheduleInstance>());
        arm.ActivateRoleAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ArmRequest());
        return arm;
    }

    private static EntraScheduleRequest GraphRequest()
        => new(
            Id: "req-1",
            Status: "Provisioned",
            Action: "selfActivate",
            PrincipalId: "prin-1",
            RoleDefinitionId: "role-groups-admin",
            DirectoryScopeId: "/",
            Justification: "incident #42",
            CreatedDateTime: DateTimeOffset.UtcNow,
            ApprovalId: null,
            RequestType: null,
            Principal: null,
            RoleDefinition: null,
            ScheduleInfo: null);

    private static ArmRoleAssignmentScheduleRequest ArmRequest()
        => new(
            Id: $"{SubScope}/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/req-arm-1",
            Name: "req-arm-1",
            Type: null,
            Properties: new ArmRoleRequestProperties(
                Status: "Provisioned",
                PrincipalId: "prin-1",
                RoleDefinitionId: "role-reader",
                Scope: SubScope,
                Justification: "incident #42",
                RequestType: "SelfActivate",
                ApprovalId: null,
                CreatedOn: DateTimeOffset.UtcNow,
                ExpandedProperties: null,
                ScheduleInfo: null,
                LinkedRoleEligibilityScheduleId: null));

    private static EligibleRolesWatcher NewWatcher(
        IGraphPimClient graph, IArmPimClient arm, INotifier notifier, ILogger? logger = null)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.Logger.Returns(logger ?? NullLogger<EligibleRolesWatcherActivationScopeTests>.Instance);
        ctx.Notifier.Returns(notifier);
        ctx.Tenants.Returns(new List<PluginTenant> { new("tenant-1", "Contoso") });

        var tenant = new PluginTenant("tenant-1", "Contoso");
        return new EligibleRolesWatcher(
            graph, arm, ctx, tenant,
            TimeSpan.FromMilliseconds(50),
            new PendingActivationStore(ctx, tenant));
    }

    // Minimal capture of the formatted log messages, so "warns and proceeds" can
    // be told apart from "silently proceeds".
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullLogger.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
