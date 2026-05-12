using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray.Auth;
using AzureTray.AzureCloud;
using AzureTray.Graph;
using Xunit;

namespace AzureTray.Tests.Graph;

public sealed class GraphMeClientTests
{
    [Fact]
    public async Task GetMeAsync_AttachesBearerHeaderAndDeserializesGraphResponse()
    {
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.OK,
            """{"id":"abc-123","displayName":"Alice Example","userPrincipalName":"alice@contoso.onmicrosoft.com","extra":"ignored"}""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") };

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(HttpClientNames.Graph).Returns(httpClient);

        var credentials = Substitute.For<ICredentialFactory>();
        credentials.GetForTenant("tenant-1").Returns(new StubCredential("token-1"));

        var cloud = Substitute.For<IAzureCloudConfig>();
        cloud.GraphScope.Returns("https://graph.microsoft.com/.default");

        var client = new GraphMeClient(factory, credentials, cloud, NullLogger<GraphMeClient>.Instance);

        var me = await client.GetMeAsync("tenant-1", CancellationToken.None);

        Assert.Equal("abc-123", me.Id);
        Assert.Equal("Alice Example", me.DisplayName);
        Assert.Equal("alice@contoso.onmicrosoft.com", me.UserPrincipalName);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(new Uri("https://graph.microsoft.com/v1.0/me"), handler.LastRequest!.RequestUri);
        Assert.NotNull(handler.LastRequest.Headers.Authorization);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("token-1", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task GetMeAsync_ThrowsOnHttpFailure()
    {
        var handler = new CapturingHttpMessageHandler(HttpStatusCode.Unauthorized, "");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") };

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(HttpClientNames.Graph).Returns(httpClient);

        var credentials = Substitute.For<ICredentialFactory>();
        credentials.GetForTenant("tenant-1").Returns(new StubCredential("token-1"));

        var cloud = Substitute.For<IAzureCloudConfig>();
        cloud.GraphScope.Returns("https://graph.microsoft.com/.default");

        var client = new GraphMeClient(factory, credentials, cloud, NullLogger<GraphMeClient>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(
            async () => await client.GetMeAsync("tenant-1", CancellationToken.None));
    }

    [Fact]
    public async Task GetMeAsync_ThrowsOnBlankTenantId()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var credentials = Substitute.For<ICredentialFactory>();
        var cloud = Substitute.For<IAzureCloudConfig>();

        var client = new GraphMeClient(factory, credentials, cloud, NullLogger<GraphMeClient>.Instance);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            async () => await client.GetMeAsync("   ", CancellationToken.None));
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public CapturingHttpMessageHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubCredential : TokenCredential
    {
        private readonly string _token;
        public StubCredential(string token) { _token = token; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(_token, DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(new AccessToken(_token, DateTimeOffset.UtcNow.AddHours(1)));
    }
}
