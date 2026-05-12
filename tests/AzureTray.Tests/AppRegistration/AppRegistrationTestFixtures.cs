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

namespace AzureTray.Tests.AppRegistration;

// Shared test fixture for the AppRegistration service tests. Builds an
// AppRegistrationGraphClient over a routing HttpMessageHandler so each
// test can declare which Graph endpoints exist.
internal static class AppRegistrationTestFixtures
{
    public const string GraphResourceAppId = "00000003-0000-0000-c000-000000000000";

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
