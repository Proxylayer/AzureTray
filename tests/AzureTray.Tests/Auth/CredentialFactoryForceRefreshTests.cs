using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using AzureTray.Auth;
using AzureTray.Configuration;
using AzureTray.Models;
using AzureTray.Tenants;
using Xunit;

namespace AzureTray.Tests.Auth;

// ForceRefreshAsync is exercised with a fake TokenCredential seeded into the
// factory's per-tenant cache — no broker, no interactive sign-in, no real STS.
// Token *values* are never logged or asserted on here; only call counts, the
// presence of a claims challenge, and ExpiresOn.
public sealed class CredentialFactoryForceRefreshTests
{
    private const string TenantId = "tenant-1";
    private const string GraphScope = "https://graph.microsoft.com/.default";
    private const string ArmScope = "https://management.azure.com/.default";

    [Fact]
    public async Task ForceRefreshAsync_NewTokenFromTheChallenge_ReturnsTrue()
    {
        var credential = new FakeCredential();
        var factory = NewFactory(credential);

        var refreshed = await factory.ForceRefreshAsync(TenantId, new[] { GraphScope }, CancellationToken.None);

        Assert.True(refreshed);
        // Once to read the current token, once with the cache-bypass challenge.
        Assert.Equal(2, credential.Calls);
        Assert.Null(credential.ClaimsSeen[0]);
        Assert.NotNull(credential.ClaimsSeen[1]);
        Assert.All(credential.ExpiresOnIssued, expires => Assert.True(expires > DateTimeOffset.UtcNow));
    }

    // Several approvals landing in one poll must not each cost an STS round-trip.
    [Fact]
    public async Task ForceRefreshAsync_RapidSecondCall_IsCollapsedByTheCooldown()
    {
        var credential = new FakeCredential();
        var factory = NewFactory(credential);

        Assert.True(await factory.ForceRefreshAsync(TenantId, new[] { GraphScope }, CancellationToken.None));
        Assert.True(await factory.ForceRefreshAsync(TenantId, new[] { GraphScope }, CancellationToken.None));

        Assert.Equal(2, credential.Calls);
    }

    [Fact]
    public async Task ForceRefreshAsync_ConcurrentCalls_AcquireOnlyOnce()
    {
        var credential = new FakeCredential { Delay = TimeSpan.FromMilliseconds(50) };
        var factory = NewFactory(credential);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ =>
                factory.ForceRefreshAsync(TenantId, new[] { GraphScope }, CancellationToken.None)));

        Assert.All(results, Assert.True);
        Assert.Equal(2, credential.Calls);
        Assert.Equal(1, credential.MaxConcurrent);
    }

    // The silent credential is DisableAutomaticAuthentication: needing the user
    // is reported, never popped from a background poll.
    [Fact]
    public async Task ForceRefreshAsync_AuthenticationRequired_ReturnsFalseInsteadOfThrowing()
    {
        var credential = new FakeCredential { FailWith = FakeFailure.AuthenticationRequired };
        var factory = NewFactory(credential);

        var refreshed = await factory.ForceRefreshAsync(TenantId, new[] { GraphScope }, CancellationToken.None);

        Assert.False(refreshed);
        Assert.Equal(1, credential.Calls);
    }

    // NOTE: the "challenge came back with the same token" path is deliberately
    // not covered. It calls Rebuild(), which constructs a real broker-backed
    // InteractiveBrowserCredential and acquires against it — untestable without
    // a credential-construction seam in CredentialFactory, and not worth
    // risking a live auth attempt from a test run.

    [Fact]
    public async Task ForceRefreshAsync_UnexpectedFailureReadingTheCurrentToken_ReturnsFalse()
    {
        var credential = new FakeCredential { FailWith = FakeFailure.Unexpected };
        var factory = NewFactory(credential);

        var refreshed = await factory.ForceRefreshAsync(TenantId, new[] { GraphScope }, CancellationToken.None);

        Assert.False(refreshed);
    }

    [Fact]
    public async Task ForceRefreshAsync_EachScopeIsRefreshed_AndDuplicatesAreCollapsed()
    {
        var credential = new FakeCredential();
        var factory = NewFactory(credential);

        Assert.True(await factory.ForceRefreshAsync(
            TenantId, new[] { GraphScope, ArmScope }, CancellationToken.None));

        Assert.Equal(4, credential.Calls);
        Assert.Equal(
            new[] { GraphScope, GraphScope, ArmScope, ArmScope },
            credential.ScopesSeen);
    }

    [Fact]
    public async Task ForceRefreshAsync_DuplicateScopes_AreDeduplicated()
    {
        var credential = new FakeCredential();
        var factory = NewFactory(credential);

        Assert.True(await factory.ForceRefreshAsync(
            TenantId, new[] { GraphScope, GraphScope }, CancellationToken.None));

        Assert.Equal(2, credential.Calls);
    }

    [Fact]
    public async Task ForceRefreshAsync_NoUsableScopes_ReturnsFalseWithoutTouchingTheCredential()
    {
        var credential = new FakeCredential();
        var factory = NewFactory(credential);

        Assert.False(await factory.ForceRefreshAsync(
            TenantId, Array.Empty<string>(), CancellationToken.None));
        Assert.False(await factory.ForceRefreshAsync(
            TenantId, new[] { "   " }, CancellationToken.None));

        Assert.Equal(0, credential.Calls);
    }

    [Fact]
    public async Task ForceRefreshAsync_BlankTenantId_Throws()
    {
        var factory = NewFactory(new FakeCredential());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            factory.ForceRefreshAsync("  ", new[] { GraphScope }, CancellationToken.None));
    }

    [Fact]
    public async Task ForceRefreshAsync_NullScopes_Throws()
    {
        var factory = NewFactory(new FakeCredential());

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            factory.ForceRefreshAsync(TenantId, null!, CancellationToken.None));
    }

    // Cancellation is the one thing that is allowed to surface to the caller.
    [Fact]
    public async Task ForceRefreshAsync_AlreadyCancelledToken_Throws()
    {
        var credential = new FakeCredential();
        var factory = NewFactory(credential);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            factory.ForceRefreshAsync(
                TenantId, new[] { GraphScope }, new CancellationToken(canceled: true)));

        Assert.Equal(0, credential.Calls);
    }

    // ---- harness ----------------------------------------------------------

    private static CredentialFactory NewFactory(TokenCredential seeded)
    {
        var tenantStore = Substitute.For<ITenantStore>();
        tenantStore.FindByTenantId(Arg.Any<string>()).Returns((Tenant?)null);

        var paths = Substitute.For<IAppPaths>();
        paths.DataDir.Returns(string.Empty);

        var factory = new CredentialFactory(
            Options.Create(new AuthOptions()),
            tenantStore,
            paths,
            NullLoggerFactory.Instance);

        SeedCredential(factory, TenantId, seeded);
        return factory;
    }

    // The factory builds real broker-backed credentials on demand, so the fake
    // has to be planted in its per-tenant cache before the first GetForTenant.
    // Reflection is the only seam; if the field is renamed this fails loudly
    // rather than silently starting an interactive sign-in.
    private static void SeedCredential(CredentialFactory factory, string tenantId, TokenCredential credential)
    {
        var field = typeof(CredentialFactory).GetField(
            "_byTenant", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var cache = field!.GetValue(factory) as ConcurrentDictionary<string, TokenCredential>;
        Assert.NotNull(cache);
        cache![tenantId] = credential;
    }

    private enum FakeFailure
    {
        None,
        AuthenticationRequired,
        Unexpected,
    }

    // Hands out a distinct token per call so a claims-challenged acquire always
    // looks like a genuine refresh. Values are opaque and never asserted on.
    private sealed class FakeCredential : TokenCredential
    {
        private readonly object _gate = new();
        private readonly List<string?> _claims = new();
        private readonly List<string> _scopes = new();
        private readonly List<DateTimeOffset> _expires = new();
        private int _calls;
        private int _concurrent;

        public TimeSpan Delay { get; init; }
        public FakeFailure FailWith { get; init; } = FakeFailure.None;

        public int Calls => Volatile.Read(ref _calls);
        public int MaxConcurrent { get; private set; }

        public string?[] ClaimsSeen { get { lock (_gate) return _claims.ToArray(); } }
        public string[] ScopesSeen { get { lock (_gate) return _scopes.ToArray(); } }
        public DateTimeOffset[] ExpiresOnIssued { get { lock (_gate) return _expires.ToArray(); } }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            var concurrent = Interlocked.Increment(ref _concurrent);
            try
            {
                lock (_gate)
                {
                    MaxConcurrent = Math.Max(MaxConcurrent, concurrent);
                    _claims.Add(requestContext.Claims);
                    _scopes.Add(requestContext.Scopes.FirstOrDefault() ?? string.Empty);
                }

                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
                }

                switch (FailWith)
                {
                    case FakeFailure.AuthenticationRequired:
                        throw new AuthenticationRequiredException("interactive sign-in required", requestContext);
                    case FakeFailure.Unexpected:
                        throw new InvalidOperationException("broker unavailable");
                    default:
                        break;
                }

                var expiresOn = DateTimeOffset.UtcNow.AddHours(1);
                lock (_gate) _expires.Add(expiresOn);
                // Opaque, per-call unique, and never surfaced anywhere.
                return new AccessToken($"fake-{call}-{Guid.NewGuid():N}", expiresOn);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }
    }
}
