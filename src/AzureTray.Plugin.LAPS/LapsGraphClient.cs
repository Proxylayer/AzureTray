using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Plugin.LAPS;

// Microsoft Graph access for Windows LAPS. Two endpoints:
//
//   GET /v1.0/directory/deviceLocalCredentials
//        ?$select=id,deviceName&$top=999
//      → enumerate LAPS-managed devices in the tenant.
//
//   GET /v1.0/directory/deviceLocalCredentials/{id}?$select=credentials
//      → fetch the credential record. passwordBase64 is base64 of UTF-8
//        bytes (NOT UTF-16 — decoding as UTF-16 yields CJK garbage).
internal sealed class LapsGraphClient
{
    private readonly IPluginContext _context;
    private readonly ILogger _logger;

    public LapsGraphClient(IPluginContext context)
    {
        _context = context;
        _logger = context.Logger;
    }

    public async Task<IReadOnlyList<LapsDevice>> ListDevicesAsync(
        string tenantId, CancellationToken cancellationToken)
    {
        var devices = new List<LapsDevice>();
        string? next = "v1.0/directory/deviceLocalCredentials?$select=id,deviceName&$top=999";

        while (next is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            using var response = await _context.GetHttpClient(tenantId).SendAsync(
                PluginHttpClientNames.Graph,
                _context.GraphScope,
                request,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                {
                    throw new HttpRequestException(
                        $"Graph denied LAPS read for tenant {tenantId} ({(int)response.StatusCode}). " +
                        "Ensure DeviceLocalCredential.Read.All is consented and the signed-in user has Cloud Device Administrator.",
                        inner: null,
                        statusCode: response.StatusCode);
                }
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"Graph /directory/deviceLocalCredentials returned {(int)response.StatusCode}: {body}",
                    inner: null,
                    statusCode: response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("value", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                    var name = item.TryGetProperty("deviceName", out var nameProp) ? nameProp.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    {
                        devices.Add(new LapsDevice(id!, name!));
                    }
                }
            }

            next = doc.RootElement.TryGetProperty("@odata.nextLink", out var nl)
                ? NormalizeNextLink(nl.GetString())
                : null;
        }

        return devices.OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<string?> GetPasswordAsync(
        string tenantId,
        string directoryRecordId,
        CancellationToken cancellationToken)
    {
        var url = $"v1.0/directory/deviceLocalCredentials/{directoryRecordId}?$select=credentials";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _context.GetHttpClient(tenantId).SendAsync(
            PluginHttpClientNames.Graph,
            _context.GraphScope,
            request,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "LAPS password fetch failed for device {DeviceId} in tenant {TenantId}: HTTP {Status} {Body}",
                directoryRecordId, tenantId, (int)response.StatusCode, body);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("credentials", out var creds) || creds.GetArrayLength() == 0)
        {
            return null;
        }

        // Graph returns credentials sorted newest-first; index 0 is the
        // current rotation.
        var latest = creds[0];
        if (!latest.TryGetProperty("passwordBase64", out var pw))
        {
            return null;
        }

        var base64 = pw.GetString();
        if (string.IsNullOrEmpty(base64)) return null;

        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "passwordBase64 was not valid base64 for device {DeviceId}", directoryRecordId);
            return null;
        }

        return Encoding.UTF8.GetString(bytes);
    }

    // Graph @odata.nextLink is absolute; the host's named "graph" client
    // already has a BaseAddress so we want a relative URL. Strip the
    // scheme+host prefix when present.
    private static string? NormalizeNextLink(string? nextLink)
    {
        if (string.IsNullOrWhiteSpace(nextLink)) return null;
        if (Uri.TryCreate(nextLink, UriKind.Absolute, out var abs))
        {
            return abs.PathAndQuery.TrimStart('/');
        }
        return nextLink;
    }
}
