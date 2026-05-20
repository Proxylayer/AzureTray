using System;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.Extensions.Logging;
using AzureTray.Auth;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Plugins;

public sealed class HostPluginHttpClient : IPluginHttpClientCore
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ICredentialFactory _credentials;
    private readonly ILogger<HostPluginHttpClient> _logger;

    public HostPluginHttpClient(
        IHttpClientFactory httpFactory,
        ICredentialFactory credentials,
        ILogger<HostPluginHttpClient> logger)
    {
        _httpFactory = httpFactory;
        _credentials = credentials;
        _logger = logger;
    }

    public async Task<HttpResponseMessage> SendCoreAsync(
        string clientName,
        string tenantId,
        string scope,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(request);

        var credential = _credentials.GetForTenant(tenantId);
        var token = await credential.GetTokenAsync(
            new TokenRequestContext([scope]),
            cancellationToken);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        var client = _httpFactory.CreateClient(clientName);

        // Trace each outgoing plugin call. Visible at Debug level — flip the
        // Log Viewer's Capture level to Debug to see this stream.
        _logger.LogDebug(
            "→ {ClientName} {Method} {Url}  (tenant {TenantId})",
            clientName, request.Method, request.RequestUri, tenantId);

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                ex,
                "× {ClientName} {Method} {Url} threw after {ElapsedMs} ms (tenant {TenantId}).",
                clientName, request.Method, request.RequestUri, stopwatch.ElapsedMilliseconds, tenantId);
            throw;
        }
        stopwatch.Stop();

        var elapsedMs = stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture);
        if ((int)response.StatusCode >= 400)
        {
            _logger.LogWarning(
                "← {ClientName} {Method} {Url}  {StatusCode} {ReasonPhrase}  ({ElapsedMs} ms, tenant {TenantId})",
                clientName, request.Method, request.RequestUri,
                (int)response.StatusCode, response.ReasonPhrase ?? string.Empty,
                elapsedMs, tenantId);
        }
        else
        {
            _logger.LogDebug(
                "← {ClientName} {Method} {Url}  {StatusCode}  ({ElapsedMs} ms, tenant {TenantId})",
                clientName, request.Method, request.RequestUri,
                (int)response.StatusCode, elapsedMs, tenantId);
        }

        return response;
    }
}
