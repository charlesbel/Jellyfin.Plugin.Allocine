using System.Buffers.Binary;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Allocine.Tests;

public sealed class AllocineMatchingSafetyTests
{
    [Fact]
    public async Task GetRatingsPropagatesRequestCancellation()
    {
        using var client = new HttpClient(new CancellableHandler());
        using var service = new AllocineService(NullLogger<AllocineService>.Instance, client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetRatings(
            "Cancelled",
            "Cancelled",
            2024,
            "Movie",
            "tt1234567",
            "123",
            cancellation.Token));
    }

    [Fact]
    public async Task ConflictingImdbAndTmdbMappingsFailClosed()
    {
        using var httpClient = new HttpClient(new RoutingHandler(request =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("P345", StringComparison.Ordinal))
            {
                return Json("{\"query\":{\"search\":[{\"title\":\"Q1\"}]}}");
            }

            if (url.Contains("P4947", StringComparison.Ordinal))
            {
                return Json("{\"query\":{\"search\":[{\"title\":\"Q2\"}]}}");
            }

            if (url.EndsWith("Q1.json", StringComparison.Ordinal))
            {
                return Json(Entity("Q1", "P345", "tt30988739", "P1265", "325941"));
            }

            if (url.EndsWith("Q2.json", StringComparison.Ordinal))
            {
                return Json(Entity("Q2", "P4947", "1233575", "P1265", "999999"));
            }

            if (request.RequestUri.Host == "android.clients.google.com")
            {
                return request.RequestUri.AbsolutePath.Contains("checkin", StringComparison.Ordinal)
                    ? CheckinResponse()
                    : Text("token=fcm-token");
            }

            if (request.RequestUri.Host == "graph.allocine.fr")
            {
                return Json("{\"data\":{\"movie\":{\"stats\":{\"userRating\":{\"score\":4.9}}}}}");
            }

            throw new InvalidOperationException($"Unexpected request: {url}");
        }));
        using var service = new AllocineService(NullLogger<AllocineService>.Instance, httpClient);

        Dictionary<string, string>? ratings = await service.GetRatings(
            "The Insider", "Black Bag", 2025, "Movie", "tt30988739", "1233575");

        Assert.Null(ratings);
    }

    [Fact]
    public async Task ConflictingProviderClaimOnSameEntityFailsClosed()
    {
        using var httpClient = new HttpClient(new RoutingHandler(request =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("w/api.php", StringComparison.Ordinal))
            {
                return Json("{\"query\":{\"search\":[{\"title\":\"Q1\"}]}}");
            }

            if (url.EndsWith("Q1.json", StringComparison.Ordinal))
            {
                return Json(EntityWithProviders("Q1", "tt30988739", "9999999", "325941"));
            }

            return RatingsDependency(request, url);
        }));
        using var service = new AllocineService(NullLogger<AllocineService>.Instance, httpClient);

        Dictionary<string, string>? ratings = await service.GetRatings(
            "The Insider", "Black Bag", 2025, "Movie", "tt30988739", "1233575");

        Assert.Null(ratings);
    }

    [Fact]
    public async Task FailedLookupForOneOfTwoProviderIdsFailsClosed()
    {
        using var httpClient = new HttpClient(new RoutingHandler(request =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("P345", StringComparison.Ordinal))
            {
                return Json("{\"query\":{\"search\":[{\"title\":\"Q1\"}]}}");
            }

            if (url.Contains("P4947", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            if (url.EndsWith("Q1.json", StringComparison.Ordinal))
            {
                return Json(EntityWithProviders("Q1", "tt30988739", "1233575", "325941"));
            }

            return RatingsDependency(request, url);
        }));
        using var service = new AllocineService(NullLogger<AllocineService>.Instance, httpClient);

        Dictionary<string, string>? ratings = await service.GetRatings(
            "The Insider", "Black Bag", 2025, "Movie", "tt30988739", "1233575");

        Assert.Null(ratings);
    }

    [Fact]
    public async Task FailedEntityFetchAfterValidCandidateFailsClosed()
    {
        using var httpClient = new HttpClient(new RoutingHandler(request =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("w/api.php", StringComparison.Ordinal))
            {
                return Json("{\"query\":{\"search\":[{\"title\":\"Q1\"},{\"title\":\"Q2\"}]}}");
            }

            if (url.EndsWith("Q1.json", StringComparison.Ordinal))
            {
                return Json(Entity("Q1", "P345", "tt30988739", "P1265", "325941"));
            }

            if (url.EndsWith("Q2.json", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return RatingsDependency(request, url);
        }));
        using var service = new AllocineService(NullLogger<AllocineService>.Instance, httpClient);

        Dictionary<string, string>? ratings = await service.GetRatings(
            "The Insider", "Black Bag", 2025, "Movie", "tt30988739", null);

        Assert.Null(ratings);
    }

    [Fact]
    public async Task TruncatedWikidataSearchFailsClosed()
    {
        using var httpClient = new HttpClient(new RoutingHandler(request =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("w/api.php", StringComparison.Ordinal))
            {
                return Json("{\"continue\":{\"sroffset\":10,\"continue\":\"-||\"},\"query\":{\"search\":[{\"title\":\"Q1\"}]}}");
            }

            if (url.EndsWith("Q1.json", StringComparison.Ordinal))
            {
                return Json(Entity("Q1", "P345", "tt30988739", "P1265", "325941"));
            }

            return RatingsDependency(request, url);
        }));
        using var service = new AllocineService(NullLogger<AllocineService>.Instance, httpClient);

        Dictionary<string, string>? ratings = await service.GetRatings(
            "The Insider", "Black Bag", 2025, "Movie", "tt30988739", null);

        Assert.Null(ratings);
    }

    [Fact]
    public async Task WikidataHttpFailureIsTransientNotConflict()
    {
        using var httpClient = new HttpClient(new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using var service = new AllocineService(NullLogger<AllocineService>.Instance, httpClient);
        var request = new AllocineRatingsRequest(
            "0123456789abcdef0123456789abcdef",
            "Movie",
            "The Insider",
            "Black Bag",
            2025,
            "tt30988739",
            "1233575");

        AllocineResolvedIdentity identity = await service.ResolveIdentityAsync(
            request,
            allowTitleYearFallback: false,
            CancellationToken.None);

        Assert.True(identity.IsTransient);
        Assert.False(identity.IsConflict);
        Assert.Null(identity.AllocineId);
    }

    [Fact]
    public async Task UnresolvedSingleProviderIdDoesNotFallBackToTitle()
    {
        using var httpClient = new HttpClient(new RoutingHandler(request =>
        {
            string url = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);
            if (url.Contains("w/api.php", StringComparison.Ordinal))
            {
                return Json("{\"query\":{\"search\":[]}}");
            }

            if (url.Contains("autocomplete", StringComparison.Ordinal))
            {
                return Json(Search("Unique Title", "Unique Title", 2025, "325941"));
            }

            return RatingsDependency(request, url);
        }));
        using var service = new AllocineService(NullLogger<AllocineService>.Instance, httpClient);

        Dictionary<string, string>? ratings = await service.GetRatings(
            "Unique Title", "Unique Title", 2025, "Movie", "tt30988739", null);

        Assert.Null(ratings);
    }

    [Fact]
    public async Task MalformedProviderIdDoesNotFallBackToTitle()
    {
        using var httpClient = new HttpClient(new RoutingHandler(request =>
        {
            string url = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);
            if (url.Contains("autocomplete", StringComparison.Ordinal))
            {
                return Json(Search("Unique Title", "Unique Title", 2025, "325941"));
            }

            return RatingsDependency(request, url);
        }));
        using var service = new AllocineService(NullLogger<AllocineService>.Instance, httpClient);

        Dictionary<string, string>? ratings = await service.GetRatings(
            "Unique Title", "Unique Title", 2025, "Movie", "not-an-imdb-id", null);

        Assert.Null(ratings);
    }

    [Fact]
    public async Task ConflictingOriginalAndLocalizedExactMatchesFailClosed()
    {
        using var httpClient = new HttpClient(new RoutingHandler(request =>
        {
            string url = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);
            if (url.Contains("autocomplete/Black Bag", StringComparison.Ordinal))
            {
                return Json(Search("The Insider", "Black Bag", 2025, "325941"));
            }

            if (url.Contains("autocomplete/The Insider", StringComparison.Ordinal))
            {
                return Json(Search("The Insider", "Different Original", 2025, "999999"));
            }

            if (request.RequestUri.Host == "android.clients.google.com")
            {
                return request.RequestUri.AbsolutePath.Contains("checkin", StringComparison.Ordinal)
                    ? CheckinResponse()
                    : Text("token=fcm-token");
            }

            if (request.RequestUri.Host == "graph.allocine.fr")
            {
                return Json("{\"data\":{\"movie\":{\"stats\":{\"userRating\":{\"score\":4.9}}}}}");
            }

            throw new InvalidOperationException($"Unexpected request: {url}");
        }));
        using var service = new AllocineService(NullLogger<AllocineService>.Instance, httpClient);

        Dictionary<string, string>? ratings = await service.GetRatings(
            "The Insider", "Black Bag", 2025, "Movie", null, null);

        Assert.Null(ratings);
    }

    private static string Entity(string entityId, string sourceProperty, string sourceValue, string targetProperty, string targetValue)
        => "{\"entities\":{\"" + entityId + "\":{\"claims\":{\"" + sourceProperty
            + "\":[{\"mainsnak\":{\"datavalue\":{\"value\":\"" + sourceValue
            + "\"}}}],\"" + targetProperty + "\":[{\"mainsnak\":{\"datavalue\":{\"value\":\""
            + targetValue + "\"}}}]}}}}";

    private static string EntityWithProviders(string entityId, string imdbId, string tmdbId, string allocineId)
        => "{\"entities\":{\"" + entityId + "\":{\"claims\":{"
            + "\"P345\":[{\"mainsnak\":{\"datavalue\":{\"value\":\"" + imdbId + "\"}}}],"
            + "\"P4947\":[{\"mainsnak\":{\"datavalue\":{\"value\":\"" + tmdbId + "\"}}}],"
            + "\"P1265\":[{\"mainsnak\":{\"datavalue\":{\"value\":\"" + allocineId + "\"}}}]}}}}";

    private static HttpResponseMessage RatingsDependency(HttpRequestMessage request, string url)
    {
        if (request.RequestUri!.Host == "android.clients.google.com")
        {
            return request.RequestUri.AbsolutePath.Contains("checkin", StringComparison.Ordinal)
                ? CheckinResponse()
                : Text("token=fcm-token");
        }

        if (request.RequestUri.Host == "graph.allocine.fr")
        {
            return Json("{\"data\":{\"movie\":{\"stats\":{\"userRating\":{\"score\":4.9}}}}}");
        }

        throw new InvalidOperationException($"Unexpected request: {url}");
    }

    private static string Search(string label, string originalLabel, int year, string id)
        => "{\"results\":[{\"entity_type\":\"movie\",\"label\":\"" + label
            + "\",\"original_label\":\"" + originalLabel + "\",\"data\":{\"year\":"
            + year.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"id\":\"" + id + "\"}}]}";

    private static HttpResponseMessage CheckinResponse()
    {
        byte[] content = new byte[18];
        content[0] = 0x39;
        BinaryPrimitives.WriteUInt64LittleEndian(content.AsSpan(1, 8), 1);
        content[9] = 0x41;
        BinaryPrimitives.WriteUInt64LittleEndian(content.AsSpan(10, 8), 2);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
    }

    private static HttpResponseMessage Json(string content)
        => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(string content)
        => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "text/plain") };

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(route(request));
    }

    private sealed class CancellableHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }
}
