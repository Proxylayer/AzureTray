using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Plugin.PIM.Http;

// HTTP transport shared by every PIM client, Microsoft Graph and ARM alike.
// The two services differ in exactly three things: which named HTTP client and
// token scope the host is asked for, how a paged response wraps its items, and
// the word that names the service in an error message. Everything else — the
// send, the JSON options, the paging loop, the next-link normalization, the
// preserve-the-body error contract — used to be written once per service and is
// now written once.
//
// Only plumbing lives here. Anything that knows a URL, a request body, or what
// a resource means belongs in the derived client, so the split stays "how we
// talk to the service" versus "what we ask it for".
internal abstract class PimHttpClientBase
{
    // Camel-case out, case-insensitive in. Graph's schema documents camelCase
    // enum values but live payloads return PascalCase for several of them
    // ("Direct" vs "direct", "Assigned" vs "assigned", "SelfActivate" vs
    // "selfActivate"), so a case-sensitive read silently loses the value. ARM
    // is no better behaved, and shares these options for the same reason.
    // WhenWritingNull is what lets a request body model an omitted property as
    // a null literal in the anonymous type.
    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IPluginContext _ctx;
    private readonly string _httpClientName;
    private readonly Func<string> _scope;
    private readonly string _serviceLabel;

    // The scope is a selector rather than a value because it is read from the
    // host on every send, exactly as it was before this base existed.
    protected PimHttpClientBase(
        IPluginContext ctx,
        string tenantId,
        string httpClientName,
        Func<string> scope,
        string serviceLabel)
    {
        _ctx = ctx;
        _httpClientName = httpClientName;
        _scope = scope;
        _serviceLabel = serviceLabel;
        Logger = ctx.Logger;
        TenantId = tenantId;
    }

    protected ILogger Logger { get; }

    protected string TenantId { get; }

    protected async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowWithBodyAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    protected async Task<T?> PostJsonAsync<T>(string url, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowWithBodyAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    protected async Task PatchJsonAsync(string url, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowWithBodyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    protected async Task<List<T>> GetAllPagesAsync<T>(string firstUrl, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        string? next = firstUrl;
        while (!string.IsNullOrEmpty(next))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await ReadPageAsync<T>(next, cancellationToken).ConfigureAwait(false);
            if (page?.Value is not null)
            {
                results.AddRange(page.Value);
            }
            next = NormalizeNextLink(page?.NextLink);
        }
        return results;
    }

    // The only part of paging the two services do not share: Graph wraps a page
    // in ODataPage<T>, ARM in ArmCollection<T>. See IPageEnvelope.
    protected abstract Task<IPageEnvelope<T>?> ReadPageAsync<T>(string url, CancellationToken cancellationToken);

    // Graph and ARM both return rich error JSON on 4xx/5xx — a code, a message,
    // and (Graph) an inner-error block. HttpResponseMessage.EnsureSuccessStatusCode
    // throws but discards the body, so we lose the only diagnostic the
    // service gives us. This helper preserves the body in the exception
    // Message so the catch site can surface it to the user / log.
    protected async Task EnsureSuccessOrThrowWithBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await ReadBodySafeAsync(response, cancellationToken).ConfigureAwait(false);
        throw BuildError(response, body);
    }

    protected static async Task<string> ReadBodySafeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return "(body unreadable)";
        }
    }

    // Named exception carrying the service's own words. Callers that need to
    // build one from a response they are inspecting themselves (ARM's self-id
    // 409 reconciliation) use this so the message never drifts from the one
    // EnsureSuccessOrThrowWithBodyAsync produces.
    protected HttpRequestException BuildError(HttpResponseMessage response, string body)
    {
        // Trim body to a sane length so a misbehaving service can't blow
        // up a log line. Graph and ARM error JSON is small in practice.
        if (body.Length > 1500) body = body[..1500] + "…(truncated)";

        return new HttpRequestException(
            $"{_serviceLabel} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} returned {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}",
            inner: null,
            statusCode: response.StatusCode);
    }

    protected static bool HasStatus(HttpRequestException ex, HttpStatusCode status)
        => ex.StatusCode == status;

    // Protected rather than private because a client that has to inspect the
    // raw response before deciding whether it failed (ARM's self-id 409
    // reconciliation) cannot go through the helpers above.
    protected Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => _ctx.GetHttpClient(TenantId).SendAsync(
            _httpClientName,
            _scope(),
            request,
            cancellationToken);

    private static string? NormalizeNextLink(string? nextLink)
    {
        if (string.IsNullOrWhiteSpace(nextLink)) return null;
        if (Uri.TryCreate(nextLink, UriKind.Absolute, out var absolute))
        {
            return absolute.PathAndQuery.TrimStart('/');
        }
        return nextLink;
    }
}
