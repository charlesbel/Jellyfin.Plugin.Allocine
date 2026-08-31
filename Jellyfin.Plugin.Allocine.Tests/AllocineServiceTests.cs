using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Allocine.Tests;

public sealed class AllocineServiceTests
{
    private const string SearchResponse = """
        {"results":[{"entity_type":"movie","label":"Le Comte de Monte-Cristo","data":{"year":2024,"id":"288404"}}]}
        """;

    [Fact]
    public async Task GetRatingsRefreshesRejectedFcmTokenThenReturnsGraphQlRatings()
    {
        var handler = new QueueHttpMessageHandler(
            JsonResponse(HttpStatusCode.OK, SearchResponse),
            CheckinResponse(1, 2),
            TextResponse(HttpStatusCode.OK, "token=first-fcm-token"),
            JsonResponse(HttpStatusCode.BadRequest, "{\"error\":\"InvalidToken\"}"),
            CheckinResponse(3, 4),
            TextResponse(HttpStatusCode.OK, "token=second-fcm-token"),
            JsonResponse(HttpStatusCode.OK, "{\"data\":{\"movie\":{\"stats\":{\"pressReview\":{\"score\":3.55},\"userRating\":{\"score\":4.47}}}}}"));
        using var httpClient = new HttpClient(handler);
        using var service = new AllocineService(NullLogger<AllocineService>.Instance, httpClient);

        Dictionary<string, string>? ratings = await service.GetRatings("Le Comte de Monte-Cristo", 2024);

        Assert.NotNull(ratings);
        Assert.Equal("3.55", ratings["presse"]);
        Assert.Equal("4.47", ratings["public"]);
        CapturedRequest[] graphRequests = handler.Requests
            .Where(request => request.RequestUri?.Host == "graph.allocine.fr")
            .ToArray();
        Assert.Equal(2, graphRequests.Length);
        Assert.Equal("first-fcm-token", graphRequests[0].AllocineAuthToken);
        Assert.Equal("second-fcm-token", graphRequests[1].AllocineAuthToken);
        Assert.All(graphRequests, request => Assert.Equal("androidapp/9.10.18", request.UserAgent));
        CapturedRequest[] googleRequests = handler.Requests
            .Where(request => request.RequestUri?.Host == "android.clients.google.com")
            .ToArray();
        Assert.All(googleRequests, request => Assert.Equal("Android-GCM/1.5", request.UserAgent));
    }

    [Fact]
    public async Task GetRatingsFallsBackToPublicPageWhenFcmRegistrationFails()
    {
        const string html = """
            <div class="rating-item-content"><span class="rating-title"> Presse </span>
            <span class="stareval-note">3,6</span></div>
            <div class="rating-item-content"><span class="rating-title"> Spectateurs </span>
            <span class="stareval-note">4,5</span></div>
            """;
        var handler = new QueueHttpMessageHandler(
            JsonResponse(HttpStatusCode.OK, SearchResponse),
            TextResponse(HttpStatusCode.ServiceUnavailable, "temporarily unavailable"),
            TextResponse(HttpStatusCode.OK, html));
        using var httpClient = new HttpClient(handler);
        using var service = new AllocineService(NullLogger<AllocineService>.Instance, httpClient);

        Dictionary<string, string>? ratings = await service.GetRatings("Le Comte de Monte-Cristo", 2024);

        Assert.NotNull(ratings);
        Assert.Equal("3.6", ratings["presse"]);
        Assert.Equal("4.5", ratings["public"]);
        Assert.Equal("www.allocine.fr", handler.Requests[^1].RequestUri!.Host);
    }

    [Fact]
    public async Task GetRatingsRefreshesFcmTokenForGraphQlAuthenticationErrorPayload()
    {
        var handler = new QueueHttpMessageHandler(
            JsonResponse(HttpStatusCode.OK, SearchResponse),
            CheckinResponse(1, 2),
            TextResponse(HttpStatusCode.OK, "token=first-fcm-token"),
            JsonResponse(HttpStatusCode.OK, "{\"errors\":[{\"message\":\"InvalidToken\"}]}"),
            CheckinResponse(3, 4),
            TextResponse(HttpStatusCode.OK, "token=second-fcm-token"),
            JsonResponse(HttpStatusCode.OK, "{\"data\":{\"movie\":{\"stats\":{\"pressReview\":{\"score\":3.55},\"userRating\":{\"score\":4.47}}}}}"));
        using var httpClient = new HttpClient(handler);
        using var service = new AllocineService(NullLogger<AllocineService>.Instance, httpClient);

        Dictionary<string, string>? ratings = await service.GetRatings("Le Comte de Monte-Cristo", 2024);

        Assert.NotNull(ratings);
        Assert.Equal("3.55", ratings["presse"]);
        CapturedRequest[] graphRequests = handler.Requests
            .Where(request => request.RequestUri?.Host == "graph.allocine.fr")
            .ToArray();
        Assert.Equal(2, graphRequests.Length);
        Assert.Equal("first-fcm-token", graphRequests[0].AllocineAuthToken);
        Assert.Equal("second-fcm-token", graphRequests[1].AllocineAuthToken);
    }

    [Fact]
    public async Task GetRatingsFailsClosedWhenFallbackIsACloudflareChallenge()
    {
        var handler = new QueueHttpMessageHandler(
            JsonResponse(HttpStatusCode.OK, SearchResponse),
            TextResponse(HttpStatusCode.ServiceUnavailable, "temporarily unavailable"),
            TextResponse(HttpStatusCode.OK, "<html><title>Just a moment...</title><div id='cf-chl-widget'></div></html>"));
        using var httpClient = new HttpClient(handler);
        using var service = new AllocineService(NullLogger<AllocineService>.Instance, httpClient);

        Dictionary<string, string>? ratings = await service.GetRatings("Le Comte de Monte-Cristo", 2024);

        Assert.Null(ratings);
    }

    private static HttpResponseMessage CheckinResponse(ulong androidId, ulong securityToken)
    {
        byte[] content = new byte[18];
        content[0] = 0x39;
        BinaryPrimitives.WriteUInt64LittleEndian(content.AsSpan(1, 8), androidId);
        content[9] = 0x41;
        BinaryPrimitives.WriteUInt64LittleEndian(content.AsSpan(10, 8), securityToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
        };
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string content)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage TextResponse(HttpStatusCode statusCode, string content)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/plain"),
        };
    }

    private sealed class QueueHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues("AC-Auth-Token", out IEnumerable<string>? values);
            Requests.Add(new CapturedRequest(
                request.RequestUri,
                values?.SingleOrDefault(),
                request.Headers.UserAgent.ToString()));
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed record CapturedRequest(Uri? RequestUri, string? AllocineAuthToken, string UserAgent);
}
