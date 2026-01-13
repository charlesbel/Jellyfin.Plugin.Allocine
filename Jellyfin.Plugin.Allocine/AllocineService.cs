using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Service to query the Allocine API using strict matching logic.
    /// </summary>
    public sealed class AllocineService : IDisposable
    {
        private const string Token = "eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.eyJpYXQiOjE2NzU0NDA1MzcsImV4cCI6MTgzMzU4MDc5OSwidXNlcm5hbWUiOiJhbm9ueW1vdXMiLCJhcHBsaWNhdGlvbl9uYW1lIjoibW9iaWxlIiwidXVpZCI6ImUwZDMxOGYzLTM0ZjAtNGVkZS05OTg0LWY4NTJiYzk0MDZjMSIsInNjb3BlIjpudWxsfQ.fsZIpQa1L6uhs7qohqOXs6PkV2Jxyz-3vWB7y6_FtqaNtjwkJkZA-vmh1FLVTnS65pWKuwy7bN_RuCq-a7R7TWCtIGE0AEAvsHX4fR0hg8u5n-6qqdmVbMk3iqskwOiuybJnqjBOUHsxsRF2pPQ9KJcvxRCfWOHoBY8qGMbxehEqOe20H-i58fQfW1P7amxoo08w0n9Mq_VxJx5Aa0rH5IHy_OEmaMQcCT7ICWD6wSxM34FyZt_IMh-EMdbuX7ML9t3YHi8f7Fu76RKFDPE3l2QFQ48X2S6hrG5k3_cw6t-JwmxicPK1-EENsEk42nja00-YO-Wk7bfPhZ1BT4VtKP48gLvb8pcFitqpTrCTjacJOMrIWvmzTLK1uUW39Ygjv8yhi9TzDfib1a6EwSChZJ8WzCpucliJW6VVDweNQ0B0CHHlDyopUgVjokHaOdQjz_zV058ZL-kK5Cg4ngfehAJMmg0d6zU6EezsKueJRUGENn6105ymW4HC2ZEN_ANbqMHIcM1dJ2lrbkNgJ8G0xGeW_LZq-d8YF2yHHd6ZwmovtSR9QJ99ZlIBX8jF60GnthkXgukQ5tu9dXcCrV6PzBb3eP5NJoUo-t4tiwgINNEyjmQT11U_mgwHGI36p-RBw7Cx_fScq4cGO2z3X5bRF508uf2nxxf_Adi7vnvwxpA";
        private const string GraphUrl = "https://graph.allocine.fr/v1/mobile/";
        private const int MaxYearDiff = 1;
        private const double MinTitleSimilarity = 0.8;

        private static readonly CompositeFormat SearchUrlFormat = CompositeFormat.Parse("https://www.allocine.fr/_/autocomplete/{0}");

        private readonly HttpClient _httpClient;
        private readonly ILogger<AllocineService> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AllocineService"/> class.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        public AllocineService(ILogger<AllocineService> logger)
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "androidapp/0.0.1");
            _logger = logger;
        }

        /// <summary>
        /// Gets the ratings for a specific movie title and year.
        /// </summary>
        /// <param name="title">The title of the movie.</param>
        /// <param name="year">The release year of the movie.</param>
        /// <returns>A dictionary containing the ratings, or null if not found.</returns>
        public async Task<Dictionary<string, string>?> GetRatings(string title, int year)
        {
            try
            {
                _logger.LogDebug("[Allocine] Requesting ratings for '{Title}' ({Year})", title, year);

                var searchResult = await SearchMovie(title, year).ConfigureAwait(false);
                if (searchResult == null)
                {
                    _logger.LogWarning("[Allocine] No valid match found for '{Title}' ({Year})", title, year);
                    return null;
                }

                return await GetMovieStats(searchResult).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Allocine] Error fetching Allocine data");
                return null;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _httpClient.Dispose();
        }

        private async Task<string?> SearchMovie(string targetTitle, int targetYear)
        {
            var encodedQuery = Uri.EscapeDataString(targetTitle);
            var url = string.Format(CultureInfo.InvariantCulture, SearchUrlFormat, encodedQuery);

            _logger.LogDebug("[Allocine] Search URL: {Url}", url);

            var response = await _httpClient.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var data = JsonNode.Parse(json);
            var results = data?["results"]?.AsArray();

            if (results == null)
            {
                _logger.LogWarning("[Allocine] API returned no 'results' array.");
                return null;
            }

            _logger.LogDebug("[Allocine] API returned {Count} candidates.", results.Count);

            string? bestId = null;
            double bestScore = 0;
            string bestCandidateTitle = string.Empty;

            foreach (var item in results)
            {
                if (item?["entity_type"]?.ToString() != "movie")
                {
                    continue;
                }

                var candidateTitle = item["label"]?.ToString() ?? string.Empty;
                var candidateYearStr = item["data"]?["year"]?.ToString() ?? "0";
                var id = item["data"]?["id"]?.ToString();

                if (!int.TryParse(candidateYearStr, out int candidateYear))
                {
                    candidateYear = 0;
                }

                _logger.LogDebug("[Allocine] Evaluating candidate: '{CandidateTitle}' ({CandidateYear}) [ID: {Id}]", candidateTitle, candidateYear, id);

                if (Math.Abs(candidateYear - targetYear) > MaxYearDiff)
                {
                    _logger.LogDebug("[Allocine] -> Discarded: Year mismatch (Target: {TargetYear}, Candidate: {CandidateYear})", targetYear, candidateYear);
                    continue;
                }

                double similarity = CalculateSimilarity(targetTitle, candidateTitle);
                _logger.LogDebug("[Allocine] -> Similarity score: {Similarity:P2}", similarity);

                if (similarity > bestScore)
                {
                    bestScore = similarity;
                    bestId = id;
                    bestCandidateTitle = candidateTitle;
                }
            }

            if (bestScore < MinTitleSimilarity)
            {
                _logger.LogWarning("[Allocine] Best match for '{TargetTitle}' ({TargetYear}) was '{BestCandidate}' with only {Score:P2} similarity. Discarding to avoid false positive.", targetTitle, targetYear, bestCandidateTitle, bestScore);
                return null;
            }

            _logger.LogInformation("[Allocine] Selected match: '{BestCandidate}' (Score: {Score:P2}) [ID: {Id}]", bestCandidateTitle, bestScore, bestId);
            return bestId;
        }

        private async Task<Dictionary<string, string>> GetMovieStats(string movieId)
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

            var request = new HttpRequestMessage(HttpMethod.Post, GraphUrl);
            request.Headers.Add("Authorization", $"Bearer {Token}");
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var node = JsonNode.Parse(json);

            var stats = node?["data"]?["movie"]?["stats"];

            var result = new Dictionary<string, string>();

            if (stats != null)
            {
                if (stats["pressReview"]?["score"] != null)
                {
                    result["presse"] = stats["pressReview"]!["score"]!.ToString();
                }

                if (stats["userRating"]?["score"] != null)
                {
                    result["public"] = stats["userRating"]!["score"]!.ToString();
                }
            }

            return result;
        }

        private static double CalculateSimilarity(string source, string target)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target))
            {
                return 0.0;
            }

            source = source.ToLowerInvariant();
            target = target.ToLowerInvariant();

            int distance = ComputeLevenshteinDistance(source, target);
            int maxLength = Math.Max(source.Length, target.Length);

            return 1.0 - ((double)distance / maxLength);
        }

        private static int ComputeLevenshteinDistance(string s, string t)
        {
            int n = s.Length;
            int m = t.Length;

            if (n == 0)
            {
                return m;
            }

            if (m == 0)
            {
                return n;
            }

            int[][] d = new int[n + 1][];
            for (int i = 0; i <= n; i++)
            {
                d[i] = new int[m + 1];
            }

            for (int i = 0; i <= n; i++)
            {
                d[i][0] = i;
            }

            for (int j = 0; j <= m; j++)
            {
                d[0][j] = j;
            }

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                    d[i][j] = Math.Min(
                        Math.Min(d[i - 1][j] + 1, d[i][j - 1] + 1),
                        d[i - 1][j - 1] + cost);
                }
            }

            return d[n][m];
        }
    }
}
