using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using AzureTray.Auth;
using Xunit;

namespace AzureTray.Tests.Auth;

public sealed class SerializedTokenCredentialTests
{
    [Fact]
    public async Task GetTokenAsync_ForwardsToInner()
    {
        var inner = new StubCredential(TimeSpan.Zero);
        using var serialized = new SerializedTokenCredential(
            inner, TimeSpan.FromSeconds(5), "tenant-1", NullLogger<SerializedTokenCredential>.Instance);

        var token = await serialized.GetTokenAsync(new TokenRequestContext(["scope"]), CancellationToken.None);

        Assert.Equal("stub-token", token.Token);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task GetTokenAsync_SerializesConcurrentCalls()
    {
        var inner = new StubCredential(TimeSpan.FromMilliseconds(100));
        using var serialized = new SerializedTokenCredential(
            inner, TimeSpan.FromSeconds(5), "tenant-1", NullLogger<SerializedTokenCredential>.Instance);

        var ctx = new TokenRequestContext(["scope"]);
        var t1 = serialized.GetTokenAsync(ctx, CancellationToken.None).AsTask();
        var t2 = serialized.GetTokenAsync(ctx, CancellationToken.None).AsTask();
        var t3 = serialized.GetTokenAsync(ctx, CancellationToken.None).AsTask();

        await Task.WhenAll(t1, t2, t3);

        Assert.Equal(1, inner.MaxConcurrent);
        Assert.Equal(3, inner.CallCount);
    }

    [Fact]
    public async Task GetTokenAsync_TimesOutWhenLockHeld()
    {
        var inner = new StubCredential(TimeSpan.FromSeconds(5));
        using var serialized = new SerializedTokenCredential(
            inner, TimeSpan.FromMilliseconds(50), "tenant-1", NullLogger<SerializedTokenCredential>.Instance);

        var ctx = new TokenRequestContext(["scope"]);
        var holder = serialized.GetTokenAsync(ctx, CancellationToken.None).AsTask();
        await Task.Delay(20);

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await serialized.GetTokenAsync(ctx, CancellationToken.None));

        await holder;
    }

    private sealed class StubCredential : TokenCredential
    {
        private readonly TimeSpan _delay;
        private int _current;

        public StubCredential(TimeSpan delay) { _delay = delay; }

        public int CallCount { get; private set; }
        public int MaxConcurrent { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            CallCount++;
            var current = Interlocked.Increment(ref _current);
            MaxConcurrent = Math.Max(MaxConcurrent, current);
            try
            {
                if (_delay > TimeSpan.Zero)
                {
                    await Task.Delay(_delay, cancellationToken);
                }
                return new AccessToken("stub-token", DateTimeOffset.UtcNow.AddHours(1));
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }
    }
}
