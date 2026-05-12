using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace AzureTray.Auth;

// Wraps a TokenCredential so token acquisitions on the same instance are serialized.
// Each tenant gets its own SerializedTokenCredential (and so its own semaphore), so a
// stuck interactive flow on one tenant does not block token acquisition on another.
public sealed class SerializedTokenCredential : TokenCredential, IDisposable
{
    private readonly TokenCredential _inner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _acquisitionTimeout;
    private readonly string _label;
    private readonly ILogger<SerializedTokenCredential> _logger;
    private bool _disposed;

    public SerializedTokenCredential(
        TokenCredential inner,
        TimeSpan acquisitionTimeout,
        string label,
        ILogger<SerializedTokenCredential> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        _inner = inner;
        _acquisitionTimeout = acquisitionTimeout;
        _label = label;
        _logger = logger;
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(_acquisitionTimeout, cancellationToken))
        {
            _logger.LogWarning(
                "Token acquisition for {Label} timed out after {TimeoutSeconds}s waiting for the lock.",
                _label, _acquisitionTimeout.TotalSeconds);
            throw new TimeoutException(
                $"Token acquisition for '{_label}' did not start within {_acquisitionTimeout.TotalSeconds}s.");
        }

        try
        {
            return await _inner.GetTokenAsync(requestContext, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _gate.Dispose();
        _disposed = true;
    }
}
