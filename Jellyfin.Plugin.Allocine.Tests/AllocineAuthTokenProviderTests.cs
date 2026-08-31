using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Text;

namespace Jellyfin.Plugin.Allocine.Tests;

public sealed class AllocineAuthTokenProviderTests
{
    [Fact]
    public async Task GetTokenAsyncRegistersAnonymousAndroidClientAndCachesToken()
    {
        const ulong androidId = 12345678901234567890;
        const ulong securityToken = 9876543210987654321;
        var handler = new QueueHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(CreateCheckinResponse(androidId, securityToken)),
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("token=fcm-token-one\r\nsubtype=848548993493", Encoding.UTF8, "text/plain"),
            });
        using var httpClient = new HttpClient(handler);
        using var provider = new AllocineAuthTokenProvider(httpClient);

        string first = await provider.GetTokenAsync(forceRefresh: false, CancellationToken.None);
        string second = await provider.GetTokenAsync(forceRefresh: false, CancellationToken.None);

        Assert.Equal("fcm-token-one", first);
        Assert.Equal(first, second);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://android.clients.google.com/checkin", handler.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Equal("application/x-protobuf", handler.Requests[0].ContentType);
        Assert.Equal("Android-GCM/1.5", handler.Requests[0].UserAgent);
        Assert.Equal("https://android.clients.google.com/c2dm/register3", handler.Requests[1].RequestUri!.AbsoluteUri);
        Assert.Equal($"AidLogin {androidId}:{securityToken}", handler.Requests[1].Authorization);
        Assert.Equal("Android-GCM/1.5", handler.Requests[1].UserAgent);
        Assert.Contains("app=com.allocine.androidapp", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("sender=848548993493", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTokenAsyncForceRefreshRegistersNewAnonymousClient()
    {
        var handler = new QueueHttpMessageHandler(
            CheckinResponse(1, 2),
            TokenResponse("first-token"),
            CheckinResponse(3, 4),
            TokenResponse("second-token"));
        using var httpClient = new HttpClient(handler);
        using var provider = new AllocineAuthTokenProvider(httpClient);

        string first = await provider.GetTokenAsync(forceRefresh: false, CancellationToken.None);
        string refreshed = await provider.GetTokenAsync(forceRefresh: true, CancellationToken.None);

        Assert.Equal("first-token", first);
        Assert.Equal("second-token", refreshed);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task GetTokenAsyncRejectsRegistrationErrors()
    {
        var handler = new QueueHttpMessageHandler(
            CheckinResponse(1, 2),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("Error=PHONE_REGISTRATION_ERROR", Encoding.UTF8, "text/plain"),
            });
        using var httpClient = new HttpClient(handler);
        using var provider = new AllocineAuthTokenProvider(httpClient);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetTokenAsync(forceRefresh: false, CancellationToken.None));

        Assert.Contains("registration", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage CheckinResponse(ulong androidId, ulong securityToken)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(CreateCheckinResponse(androidId, securityToken)),
        };
    }

    private static HttpResponseMessage TokenResponse(string token)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"token={token}", Encoding.UTF8, "text/plain"),
        };
    }

    private static byte[] CreateCheckinResponse(ulong androidId, ulong securityToken)
    {
        byte[] response = new byte[18];
        response[0] = 0x39;
        BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(1, 8), androidId);
        response[9] = 0x41;
        BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(10, 8), securityToken);
        return response;
    }

    private sealed class QueueHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.RequestUri,
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.Authorization?.ToString(),
                request.Headers.UserAgent.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return _responses.Dequeue();
        }
    }

    private sealed record CapturedRequest(
        Uri? RequestUri,
        string? ContentType,
        string? Authorization,
        string UserAgent,
        string Body);
}
