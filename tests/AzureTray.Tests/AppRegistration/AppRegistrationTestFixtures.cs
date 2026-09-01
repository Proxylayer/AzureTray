using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using NSubstitute;
using AzureTray.AppRegistration.Internal;
using AzureTray.Auth;
using AzureTray.AzureCloud;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Tests.AppRegistration;

// Shared test fixture for the AppRegistration service tests. Builds an
// AppRegistrationGraphClient over a routing HttpMessageHandler so each
// test can declare which Graph endpoints exist.
internal static class AppRegistrationTestFixtures
{
    public const string GraphResourceAppId = "00000003-0000-0000-c000-000000000000";
    public const string ArmResourceAppId = "797f4846-ba00-4fd7-ba43-dac1f8f63013";

    // Scope ids must be GUIDs. The Graph-facing layer drops any requirement
    // whose ScopeId does not parse as one (RequiredPermissionsAggregator.
    // KeepValid), because Graph rejects the whole request otherwise - so a
    // placeholder id like "id-user-read" silently filters a fixture down to
    // nothing and the test then asserts against an empty result.
    // Deliberately fake but well-formed, and stable so the JSON fixtures can
    // interpolate the same value the requirement carries. Not real Microsoft
    // scope ids: nothing here depends on Graph's actual GUIDs.
    public const string UserReadScopeId = "11111111-1111-4111-8111-111111111111";
    public const string RoleManagementReadDirectoryScopeId = "22222222-2222-4222-8222-222222222222";

    public static PluginPermissionRequirement GraphRequirement(string name, string id)
        => new(PermissionApi.MicrosoftGraph, name, id, name);

    public static AppRegistrationGraphClient NewGraphClient(RoutedHttpHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(HttpClientNames.Graph).Returns(client);

        var credentials = Substitute.For<ICredentialFactory>();
        credentials.GetForTenant(Arg.Any<string>()).Returns(new StubCredential());

        var cloud = Substitute.For<IAzureCloudConfig>();
        cloud.GraphScope.Returns("https://graph.microsoft.com/.default");

        return new AppRegistrationGraphClient(factory, credentials, cloud);
    }

    public static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    // A small routing handler so each test sets up specific endpoints.
    // Supports all verbs and records every request that ran (with body)
    // so tests can assert on what was sent.
    public sealed class RoutedHttpHandler : HttpMessageHandler
    {
        private readonly List<(Predicate<HttpRequestMessage> Match, Func<HttpRequestMessage, HttpResponseMessage> Reply)> _routes = new();
        public List<RecordedRequest> Recorded { get; } = new();

        public void OnGet(string urlContains, Func<HttpRequestMessage, HttpResponseMessage> reply)
            => Add(HttpMethod.Get, r => r.RequestUri!.ToString().StartsWith(urlContains, StringComparison.OrdinalIgnoreCase), reply);

        public void OnGet(Predicate<Uri> match, Func<HttpRequestMessage, HttpResponseMessage> reply)
            => Add(HttpMethod.Get, r => match(r.RequestUri!), reply);

        public void OnPost(Predicate<Uri> match, Func<HttpRequestMessage, HttpResponseMessage> reply)
            => Add(HttpMethod.Post, r => match(r.RequestUri!), reply);

        public void OnPatch(Predicate<Uri> match, Func<HttpRequestMessage, HttpResponseMessage> reply)
            => Add(HttpMethod.Patch, r => match(r.RequestUri!), reply);

        public void OnDelete(Predicate<Uri> match, Func<HttpRequestMessage, HttpResponseMessage> reply)
            => Add(HttpMethod.Delete, r => match(r.RequestUri!), reply);

        private void Add(HttpMethod method, Predicate<HttpRequestMessage> match, Func<HttpRequestMessage, HttpResponseMessage> reply)
        {
            _routes.Add((r => r.Method == method && match(r), reply));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Recorded.Add(new RecordedRequest(request.Method, request.RequestUri!, body));

            foreach (var (match, reply) in _routes)
            {
                if (match(request)) return reply(request);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"No route matched {request.Method} {request.RequestUri}", Encoding.UTF8, "text/plain"),
            };
        }
    }

    public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body);

    private sealed class StubCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new("test-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1)));
    }
}
