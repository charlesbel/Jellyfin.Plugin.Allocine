using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Service to query the Allocine API using strict matching logic.
    /// </summary>
    public sealed class AllocineService : IAllocineMappingProvider, IDisposable
    {
        private const string Token = "eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.eyJpYXQiOjE2NzU0NDA1MzcsImV4cCI6MTgzMzU4MDc5OSwidXNlcm5hbWUiOiJhbm9ueW1vdXMiLCJhcHBsaWNhdGlvbl9uYW1lIjoibW9iaWxlIiwidXVpZCI6ImUwZDMxOGYzLTM0ZjAtNGVkZS05OTg0LWY4NTJiYzk0MDZjMSIsInNjb3BlIjpudWxsfQ.fsZIpQa1L6uhs7qohqOXs6PkV2Jxyz-3vWB7y6_FtqaNtjwkJkZA-vmh1FLVTnS65pWKuwy7bN_RuCq-a7R7TWCtIGE0AEAvsHX4fR0hg8u5n-6qqdmVbMk3iqskwOiuybJnqjBOUHsxsRF2pPQ9KJcvxRCfWOHoBY8qGMbxehEqOe20H-i58fQfW1P7amxoo08w0n9Mq_VxJx5Aa0rH5IHy_OEmaMQcCT7ICWD6wSxM34FyZt_IMh-EMdbuX7ML9t3YHi8f7Fu76RKFDPE3l2QFQ48X2S6hrG5k3_cw6t-JwmxicPK1-EENsEk42nja00-YO-Wk7bfPhZ1BT4VtKP48gLvb8pcFitqpTrCTjacJOMrIWvmzTLK1uUW39Ygjv8yhi9TzDfib1a6EwSChZJ8WzCpucliJW6VVDweNQ0B0CHHlDyopUgVjokHaOdQjz_zV058ZL-kK5Cg4ngfehAJMmg0d6zU6EezsKueJRUGENn6105ymW4HC2ZEN_ANbqMHIcM1dJ2lrbkNgJ8G0xGeW_LZq-d8YF2yHHd6ZwmovtSR9QJ99ZlIBX8jF60GnthkXgukQ5tu9dXcCrV6PzBb3eP5NJoUo-t4tiwgINNEyjmQT11U_mgwHGI36p-RBw7Cx_fScq4cGO2z3X5bRF508uf2nxxf_Adi7vnvwxpA";
        private const string GraphUrl = "https://graph.allocine.fr/v1/mobile/";
        private const string MobileUserAgent = "androidapp/9.10.18";
        private static readonly CompositeFormat SearchUrlFormat = CompositeFormat.Parse("https://www.allocine.fr/_/autocomplete/{0}");
        private static readonly CompositeFormat PublicMovieUrlFormat = CompositeFormat.Parse("https://www.allocine.fr/film/fichefilm_gen_cfilm={0}.html");
        private static readonly CompositeFormat PublicSeriesUrlFormat = CompositeFormat.Parse("https://www.allocine.fr/series/ficheserie_gen_cserie={0}.html");
        private static readonly CompositeFormat WikidataSearchUrlFormat = CompositeFormat.Parse("https://www.wikidata.org/w/api.php?action=query&list=search&srnamespace=0&format=json&srsearch={0}");
        private static readonly CompositeFormat WikidataEntityUrlFormat = CompositeFormat.Parse("https://www.wikidata.org/wiki/Special:EntityData/{0}.json");

        private readonly HttpClient _httpClient;
        private readonly AllocineAuthTokenProvider _authTokenProvider;
        private readonly ILogger<AllocineService> _logger;
        private readonly SemaphoreSlim _wikidataGate = new(1, 1);
        private readonly TimeSpan _wikidataPacing;
        private DateTimeOffset _nextWikidataRequestAt = DateTimeOffset.MinValue;

        /// <summary>
        /// Initializes a new instance of the <see cref="AllocineService"/> class.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        public AllocineService(ILogger<AllocineService> logger)
            : this(logger, new HttpClient(), TimeSpan.FromSeconds(1))
        {
        }

        internal AllocineService(ILogger<AllocineService> logger, HttpClient httpClient)
            : this(logger, httpClient, TimeSpan.Zero)
        {
        }

        internal AllocineService(
            ILogger<AllocineService> logger,
            HttpClient httpClient,
            TimeSpan wikidataPacing)
        {
            _httpClient = httpClient;
            _authTokenProvider = new AllocineAuthTokenProvider(_httpClient);
            _logger = logger;
            _wikidataPacing = wikidataPacing;
        }

        /// <summary>
        /// Gets the ratings for a specific movie title and year.
        /// </summary>
        /// <param name="title">The title of the movie.</param>
        /// <param name="year">The release year of the movie.</param>
        /// <returns>A dictionary containing the ratings, or null if not found.</returns>
        public async Task<Dictionary<string, string>?> GetRatings(string title, int year)
        {
            return await GetRatings(title, null, year, "Movie", null, null, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets ratings using stable external identifiers when available.
        /// </summary>
        /// <param name="title">The localized media title.</param>
        /// <param name="originalTitle">The original media title, when known.</param>
        /// <param name="year">The production year.</param>
        /// <param name="mediaType">The Jellyfin media type, Movie or Series.</param>
        /// <param name="imdbId">The IMDb identifier, when known.</param>
        /// <param name="tmdbId">The TMDb identifier, when known.</param>
        /// <param name="cancellationToken">The request cancellation token.</param>
        /// <returns>A dictionary containing the ratings, or null if no unambiguous match exists.</returns>
        public async Task<Dictionary<string, string>?> GetRatings(
            string title,
            string? originalTitle,
            int year,
            string mediaType,
            string? imdbId,
            string? tmdbId,
            CancellationToken cancellationToken = default)
        {
            var request = new AllocineRatingsRequest(
                string.Empty,
                mediaType,
                title,
                originalTitle,
                year,
                imdbId,
                tmdbId);
            string? allocineId = await ResolveAllocineIdAsync(request, cancellationToken).ConfigureAwait(false);
            return allocineId == null
                ? null
                : await GetRatingsByAllocineIdAsync(allocineId, mediaType, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<string?> ResolveAllocineIdAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                bool isSeries = request.MediaType.Equals("Series", StringComparison.OrdinalIgnoreCase);
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "[Allocine] Resolving {MediaType} '{Title}' ({Year})",
                        isSeries ? "series" : "movie",
                        request.Title,
                        request.Year);
                }

                IdResolution resolution = await ResolveAllocineId(
                    request.ImdbId,
                    request.TmdbId,
                    isSeries,
                    cancellationToken).ConfigureAwait(false);
                if (resolution.IsConflict)
                {
                    _logger.LogWarning("[Allocine] Conflicting external identifiers; refusing to select a media.");
                    return null;
                }

                string? allocineId = resolution.Id;
                allocineId ??= await SearchMedia(
                    request.Title,
                    request.OriginalTitle,
                    request.Year,
                    isSeries,
                    cancellationToken).ConfigureAwait(false);
                if (allocineId == null)
                {
                    _logger.LogWarning("[Allocine] No valid match found for '{Title}' ({Year})", request.Title, request.Year);
                }

                return allocineId;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Allocine] Error resolving exact AlloCiné identity");
                return null;
            }
        }

        /// <inheritdoc />
        public async Task<Dictionary<string, string>?> GetRatingsByAllocineIdAsync(
            string allocineId,
            string mediaType,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!Regex.IsMatch(allocineId, "^[1-9][0-9]{0,11}$", RegexOptions.CultureInvariant)
                    || (!mediaType.Equals("Movie", StringComparison.OrdinalIgnoreCase)
                        && !mediaType.Equals("Series", StringComparison.OrdinalIgnoreCase)))
                {
                    return null;
                }

                bool isSeries = mediaType.Equals("Series", StringComparison.OrdinalIgnoreCase);
                return isSeries
                    ? await GetStatsFromPublicPage(allocineId, true, cancellationToken).ConfigureAwait(false)
                    : await GetMovieStats(allocineId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Allocine] Error fetching ratings for a validated AlloCiné identity");
                return null;
            }
        }

        /// <inheritdoc />
        public Task<Dictionary<string, string>?> GetRatingsAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            return GetRatings(
                request.Title,
                request.OriginalTitle,
                request.Year,
                request.MediaType,
                request.ImdbId,
                request.TmdbId,
                cancellationToken);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _authTokenProvider.Dispose();
            _httpClient.Dispose();
            _wikidataGate.Dispose();
        }

        private async Task<HttpResponseMessage> SendWikidataGetAsync(
            string url,
            CancellationToken cancellationToken)
        {
            await _wikidataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    TimeSpan pacingDelay = _nextWikidataRequestAt - DateTimeOffset.UtcNow;
                    if (pacingDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(pacingDelay, cancellationToken).ConfigureAwait(false);
                    }

                    _nextWikidataRequestAt = DateTimeOffset.UtcNow + _wikidataPacing;
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.UserAgent.ParseAdd("Jellyfin.Plugin.Allocine/0.4.6 (+https://github.com/charlesbel/Jellyfin.Plugin.Allocine)");
                    HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= 1)
                    {
                        return response;
                    }

                    TimeSpan retryDelay = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                        ?? _wikidataPacing;
                    if (retryDelay < TimeSpan.Zero)
                    {
                        retryDelay = TimeSpan.Zero;
                    }

                    if (retryDelay > TimeSpan.FromMinutes(1))
                    {
                        return response;
                    }

                    response.Dispose();
                    if (retryDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _wikidataGate.Release();
            }
        }

        private async Task<IdResolution> ResolveAllocineId(
            string? imdbId,
            string? tmdbId,
            bool isSeries,
            CancellationToken cancellationToken)
        {
            var identifiers = new List<(string Property, string Value)>();
            var resolvedIds = new HashSet<string>(StringComparer.Ordinal);
            var queryResolvedIds = new List<HashSet<string>>();
            var queryCompleteness = new List<bool>();
            bool imdbSupplied = !string.IsNullOrWhiteSpace(imdbId);
            bool tmdbSupplied = !string.IsNullOrWhiteSpace(tmdbId);
            if (imdbSupplied && !Regex.IsMatch(imdbId!, "^tt[0-9]{7,9}$", RegexOptions.CultureInvariant))
            {
                return new IdResolution(null, true);
            }

            if (tmdbSupplied && !Regex.IsMatch(tmdbId!, "^[1-9][0-9]{0,9}$", RegexOptions.CultureInvariant))
            {
                return new IdResolution(null, true);
            }

            if (imdbSupplied)
            {
                identifiers.Add(("P345", imdbId!));
            }

            if (tmdbSupplied)
            {
                identifiers.Add((isSeries ? "P4983" : "P4947", tmdbId!));
            }

            foreach ((string property, string value) in identifiers)
            {
                var currentQueryIds = new HashSet<string>(StringComparer.Ordinal);
                bool currentQueryComplete = true;
                try
                {
                    string searchExpression = $"haswbstatement:{property}={value}";
                    string searchUrl = string.Format(
                        CultureInfo.InvariantCulture,
                        WikidataSearchUrlFormat,
                        Uri.EscapeDataString(searchExpression));
                    using HttpResponseMessage searchResponse = await SendWikidataGetAsync(searchUrl, cancellationToken).ConfigureAwait(false);
                    searchResponse.EnsureSuccessStatusCode();
                    JsonNode? searchData = JsonNode.Parse(await searchResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                    if (searchData?["continue"] != null)
                    {
                        currentQueryComplete = false;
                    }

                    JsonArray? searchResults = searchData?["query"]?["search"]?.AsArray();
                    if (searchResults == null)
                    {
                        continue;
                    }

                    foreach (JsonNode? result in searchResults)
                    {
                        string entityId = result?["title"]?.ToString() ?? string.Empty;
                        if (!Regex.IsMatch(entityId, "^Q[1-9][0-9]*$", RegexOptions.CultureInvariant))
                        {
                            continue;
                        }

                        string entityUrl = string.Format(CultureInfo.InvariantCulture, WikidataEntityUrlFormat, entityId);
                        using HttpResponseMessage entityResponse = await SendWikidataGetAsync(entityUrl, cancellationToken).ConfigureAwait(false);
                        entityResponse.EnsureSuccessStatusCode();
                        JsonNode? entityData = JsonNode.Parse(await entityResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                        JsonNode? claims = entityData?["entities"]?[entityId]?["claims"];
                        if (!identifiers.All(identifier => ClaimContains(claims, identifier.Property, identifier.Value)))
                        {
                            continue;
                        }

                        string allocineProperty = isSeries ? "P1267" : "P1265";
                        foreach (string allocineId in ClaimValues(claims, allocineProperty))
                        {
                            if (Regex.IsMatch(allocineId, "^[1-9][0-9]{0,11}$", RegexOptions.CultureInvariant))
                            {
                                currentQueryIds.Add(allocineId);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    currentQueryComplete = false;
                    _logger.LogWarning(ex, "[Allocine] Exact identifier resolution failed for {Property}; trying the next safe strategy.", property);
                }

                queryCompleteness.Add(currentQueryComplete);
                queryResolvedIds.Add(currentQueryIds);
                resolvedIds.UnionWith(currentQueryIds);
            }

            if (queryCompleteness.Any(isComplete => !isComplete)
                || (identifiers.Count > 1 && queryResolvedIds.Any(ids => ids.Count == 0))
                || (identifiers.Count > 0 && resolvedIds.Count == 0)
                || resolvedIds.Count > 1)
            {
                return new IdResolution(null, true);
            }

            string? exactId = resolvedIds.SingleOrDefault();
            if (exactId != null && _logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("[Allocine] Resolved exact AlloCiné ID {Id} from external identifiers.", exactId);
            }

            return new IdResolution(exactId, false);
        }

        private async Task<string?> SearchMedia(
            string targetTitle,
            string? originalTitle,
            int targetYear,
            bool isSeries,
            CancellationToken cancellationToken)
        {
            var exactIds = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(originalTitle)
                && !NormalizeTitle(originalTitle).Equals(NormalizeTitle(targetTitle), StringComparison.Ordinal))
            {
                exactIds.UnionWith(await SearchMediaBySingleTitle(originalTitle, targetYear, isSeries, cancellationToken).ConfigureAwait(false));
            }

            exactIds.UnionWith(await SearchMediaBySingleTitle(targetTitle, targetYear, isSeries, cancellationToken).ConfigureAwait(false));
            if (exactIds.Count != 1)
            {
                _logger.LogWarning(
                    "[Allocine] Exact title/year fallback produced {Count} distinct matches; refusing an ambiguous result.",
                    exactIds.Count);
                return null;
            }

            string exactId = exactIds.First();
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("[Allocine] Selected exact title/year fallback [ID: {Id}]", exactId);
            }

            return exactId;
        }

        private async Task<HashSet<string>> SearchMediaBySingleTitle(
            string targetTitle,
            int targetYear,
            bool isSeries,
            CancellationToken cancellationToken)
        {
            var encodedQuery = Uri.EscapeDataString(targetTitle);
            var url = string.Format(CultureInfo.InvariantCulture, SearchUrlFormat, encodedQuery);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("[Allocine] Search URL: {Url}", url);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(MobileUserAgent);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var data = JsonNode.Parse(json);
            var results = data?["results"]?.AsArray();

            if (results == null)
            {
                _logger.LogWarning("[Allocine] API returned no 'results' array.");
                return [];
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("[Allocine] API returned {Count} candidates.", results.Count);
            }

            var exactIds = new HashSet<string>(StringComparer.Ordinal);
            string expectedEntityType = isSeries ? "series" : "movie";
            string normalizedTarget = NormalizeTitle(targetTitle);

            foreach (var item in results)
            {
                if (!expectedEntityType.Equals(item?["entity_type"]?.ToString(), StringComparison.Ordinal))
                {
                    continue;
                }

                var candidateTitle = item["label"]?.ToString() ?? string.Empty;
                var candidateOriginalTitle = item["original_label"]?.ToString() ?? string.Empty;
                var candidateYearStr = item["data"]?["year"]?.ToString() ?? "0";
                var id = item["data"]?["id"]?.ToString();

                if (!int.TryParse(candidateYearStr, out int candidateYear))
                {
                    candidateYear = 0;
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("[Allocine] Evaluating candidate: '{CandidateTitle}' ({CandidateYear}) [ID: {Id}]", candidateTitle, candidateYear, id);
                }

                if (candidateYear != targetYear)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("[Allocine] -> Discarded: Year mismatch (Target: {TargetYear}, Candidate: {CandidateYear})", targetYear, candidateYear);
                    }

                    continue;
                }

                bool exactTitle = normalizedTarget.Equals(NormalizeTitle(candidateTitle), StringComparison.Ordinal)
                    || normalizedTarget.Equals(NormalizeTitle(candidateOriginalTitle), StringComparison.Ordinal);
                if (exactTitle && id != null && Regex.IsMatch(id, "^[1-9][0-9]{0,11}$", RegexOptions.CultureInvariant))
                {
                    exactIds.Add(id);
                }
            }

            return exactIds;
        }

        private async Task<Dictionary<string, string>?> GetMovieStats(string movieId, CancellationToken cancellationToken)
        {
            try
            {
                Dictionary<string, string>? graphRatings = await GetMovieStatsFromGraphQl(movieId, cancellationToken).ConfigureAwait(false);
                if (graphRatings != null)
                {
                    return graphRatings;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Allocine] Authenticated GraphQL ratings failed; trying the public page fallback.");
            }

            return await GetStatsFromPublicPage(movieId, false, cancellationToken).ConfigureAwait(false);
        }

        private async Task<Dictionary<string, string>?> GetMovieStatsFromGraphQl(
            string movieId,
            CancellationToken cancellationToken)
        {
            var rawId = $"Movie:{movieId}";
            var encodedId = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawId));
            var query = @"query MovieMini($id: String) {
              movie(id: $id) {
                stats {
                  userRating { score(base: 5) }
                  pressReview { score(base: 5) }
                }
              }
            }";
            var payload = new
            {
                query,
                variables = new { id = encodedId }
            };

            for (int attempt = 0; attempt < 2; attempt++)
            {
                string authToken = await _authTokenProvider
                    .GetTokenAsync(forceRefresh: attempt > 0, cancellationToken)
                    .ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Post, GraphUrl);
                request.Headers.UserAgent.ParseAdd(MobileUserAgent);
                request.Headers.Add("Authorization", $"Bearer {Token}");
                request.Headers.Add("AC-Auth-Token", authToken);
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "[Allocine] GraphQL ratings returned HTTP {StatusCode} on attempt {Attempt}.",
                        (int)response.StatusCode,
                        attempt + 1);
                    if (attempt == 0 && IsAuthenticationFailure(response.StatusCode))
                    {
                        continue;
                    }

                    return null;
                }

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                Dictionary<string, string> result = ParseGraphQlRatings(json, out bool authenticationError);
                if (attempt == 0 && authenticationError)
                {
                    continue;
                }

                return result.Count == 0 ? null : result;
            }

            return null;
        }

        private async Task<Dictionary<string, string>?> GetStatsFromPublicPage(
            string mediaId,
            bool isSeries,
            CancellationToken cancellationToken)
        {
            string url = string.Format(
                CultureInfo.InvariantCulture,
                isSeries ? PublicSeriesUrlFormat : PublicMovieUrlFormat,
                mediaId);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("[Allocine] Using public {MediaType} page for ID {Id}.", isSeries ? "series" : "movie", mediaId);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(MobileUserAgent);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Dictionary<string, string>? ratings = AllocineRatingsParser.Parse(html);
            if (ratings == null)
            {
                _logger.LogWarning("[Allocine] Public movie page did not contain usable ratings.");
            }

            return ratings;
        }

        private static Dictionary<string, string> ParseGraphQlRatings(string json, out bool authenticationError)
        {
            var node = JsonNode.Parse(json);
            var stats = node?["data"]?["movie"]?["stats"];
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            authenticationError = HasAuthenticationError(node);

            if (stats?["pressReview"]?["score"] != null)
            {
                result["presse"] = stats["pressReview"]!["score"]!.ToString();
            }

            if (stats?["userRating"]?["score"] != null)
            {
                result["public"] = stats["userRating"]!["score"]!.ToString();
            }

            return result;
        }

        private static bool HasAuthenticationError(JsonNode? node)
        {
            if (node?["errors"] is not JsonArray errors)
            {
                return false;
            }

            foreach (JsonNode? error in errors)
            {
                string message = error?["message"]?.ToString() ?? string.Empty;
                if (message.Contains("token", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("auth", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsAuthenticationFailure(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.BadRequest
                || statusCode == HttpStatusCode.Unauthorized
                || statusCode == HttpStatusCode.Forbidden;
        }

        private static bool ClaimContains(JsonNode? claims, string property, string expectedValue)
        {
            if (claims?[property] is not JsonArray values)
            {
                return false;
            }

            return values.Any(value => expectedValue.Equals(
                value?["mainsnak"]?["datavalue"]?["value"]?.ToString(),
                StringComparison.Ordinal));
        }

        private static IEnumerable<string> ClaimValues(JsonNode? claims, string property)
        {
            if (claims?[property] is not JsonArray values)
            {
                yield break;
            }

            foreach (JsonNode? value in values)
            {
                string? claimValue = value?["mainsnak"]?["datavalue"]?["value"]?.ToString();
                if (claimValue != null)
                {
                    yield return claimValue;
                }
            }
        }

        private static string NormalizeTitle(string value)
        {
            string decomposed = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (char character in decomposed)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
                if (category != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character))
                {
                    builder.Append(char.ToLowerInvariant(character));
                }
            }

            return builder.ToString();
        }

        private readonly record struct IdResolution(string? Id, bool IsConflict);
    }
}
