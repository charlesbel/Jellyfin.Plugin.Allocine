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

    [Fact]
    public async Task GetRatingsUsesExactWikidataMovieIdInsteadOfAmbiguousTitleSearch()
    {
        const string wikidataSearch = """
            {"query":{"search":[{"title":"Q124373035"}]}}
            """;
        const string wikidataEntity = """
            {"entities":{"Q124373035":{"claims":{"P345":[{"mainsnak":{"datavalue":{"value":"tt30988739"}}}],"P4947":[{"mainsnak":{"datavalue":{"value":"1233575"}}}],"P1265":[{"mainsnak":{"datavalue":{"value":"325941"}}}]}}}}
            """;
        var handler = new QueueHttpMessageHandler(
            JsonResponse(HttpStatusCode.OK, wikidataSearch),
            JsonResponse(HttpStatusCode.OK, wikidataEntity),
            JsonResponse(HttpStatusCode.OK, wikidataSearch),
            JsonResponse(HttpStatusCode.OK, wikidataEntity),
            CheckinResponse(1, 2),
            TextResponse(HttpStatusCode.OK, "token=fcm-token"),
            JsonResponse(HttpStatusCode.OK, "{\"data\":{\"movie\":{\"stats\":{\"userRating\":{\"score\":3.8}}}}}"));
        using var httpClient = new HttpClient(handler);
        using var service = new AllocineService(NullLogger<AllocineService>.Instance, httpClient);

        Dictionary<string, string>? ratings = await service.GetRatings(
            "The Insider",
            "Black Bag",
            2025,
            "Movie",
            "tt30988739",
            "1233575");

        Assert.NotNull(ratings);
        Assert.Equal("3.8", ratings["public"]);
        Assert.DoesNotContain(handler.Requests, request => request.RequestUri?.AbsolutePath.Contains("autocomplete", StringComparison.Ordinal) == true);
        CapturedRequest graphRequest = Assert.Single(handler.Requests, request => request.RequestUri?.Host == "graph.allocine.fr");
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("Movie:325941")), graphRequest.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRatingsSupportsSeriesUsingExactWikidataIdAndPublicSeriesPage()
    {
        const string wikidataSearch = """
            {"query":{"search":[{"title":"Q96407897"}]}}
            """;
        const string wikidataEntity = """
            {"entities":{"Q96407897":{"claims":{"P345":[{"mainsnak":{"datavalue":{"value":"tt10986410"}}}],"P4983":[{"mainsnak":{"datavalue":{"value":"97546"}}}],"P1267":[{"mainsnak":{"datavalue":{"value":"25762"}}}]}}}}
            """;
        const string html = """
            <div class="rating-item-content"><span class="rating-title"> Spectateurs </span>
            <span class="stareval-note">4,4</span></div>
            """;
        var handler = new QueueHttpMessageHandler(
            JsonResponse(HttpStatusCode.OK, wikidataSearch),
            JsonResponse(HttpStatusCode.OK, wikidataEntity),
            JsonResponse(HttpStatusCode.OK, wikidataSearch),
            JsonResponse(HttpStatusCode.OK, wikidataEntity),
            TextResponse(HttpStatusCode.OK, html));
        using var httpClient = new HttpClient(handler);
        using var service = new AllocineService(NullLogger<AllocineService>.Instance, httpClient);

        Dictionary<string, string>? ratings = await service.GetRatings(
            "Ted Lasso",
            "Ted Lasso",
            2020,
            "Series",
            "tt10986410",
            "97546");

        Assert.NotNull(ratings);
        Assert.Equal("4.4", ratings["public"]);
        Assert.Equal(
            "https://www.allocine.fr/series/ficheserie_gen_cserie=25762.html",
            handler.Requests[^1].RequestUri?.AbsoluteUri);
        Assert.DoesNotContain(handler.Requests, request => request.RequestUri?.Host == "graph.allocine.fr");
    }

    [Fact]
    public async Task ExactIdentityResolutionRetriesOneWikidataRateLimitResponse()
    {
        const string wikidataSearch = """
            {"query":{"search":[{"title":"Q124373035"}]}}
            """;
        const string wikidataEntity = """
            {"entities":{"Q124373035":{"claims":{"P345":[{"mainsnak":{"datavalue":{"value":"tt30988739"}}}],"P1265":[{"mainsnak":{"datavalue":{"value":"325941"}}}]}}}}
            """;
        HttpResponseMessage rateLimited = JsonResponse(HttpStatusCode.TooManyRequests, "{}");
        rateLimited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
        var handler = new QueueHttpMessageHandler(
            rateLimited,
            JsonResponse(HttpStatusCode.OK, wikidataSearch),
            JsonResponse(HttpStatusCode.OK, wikidataEntity));
        using var httpClient = new HttpClient(handler);
        using var service = new AllocineService(
            NullLogger<AllocineService>.Instance,
            httpClient,
            TimeSpan.Zero);

        string? allocineId = await service.ResolveAllocineIdAsync(
            new AllocineRatingsRequest("item", "Movie", "Black Bag", "Black Bag", 2025, "tt30988739", null),
            CancellationToken.None);

        Assert.Equal("325941", allocineId);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(
            handler.Requests,
            request => Assert.Contains(
                "+https://github.com/charlesbel/Jellyfin.Plugin.Allocine",
                request.UserAgent,
                StringComparison.Ordinal));
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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.TryGetValues("AC-Auth-Token", out IEnumerable<string>? values);
            Requests.Add(new CapturedRequest(
                request.RequestUri,
                values?.SingleOrDefault(),
                request.Headers.UserAgent.ToString(),
                request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return _responses.Dequeue();
        }
    }

    private sealed record CapturedRequest(Uri? RequestUri, string? AllocineAuthToken, string UserAgent, string Body);
}
