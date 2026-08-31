using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Extracts ratings from the public Allocine movie page fallback.
    /// </summary>
    internal static partial class AllocineRatingsParser
    {
        private static readonly string[] ChallengeMarkers =
        [
            "cf-chl-",
            "challenge-platform",
            "Attention Required",
            "Just a moment...",
        ];

        public static Dictionary<string, string>? Parse(string html)
        {
            ArgumentNullException.ThrowIfNull(html);

            foreach (string marker in ChallengeMarkers)
            {
                if (html.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            var ratings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match match in RatingRegex().Matches(html))
            {
                string scoreText = match.Groups["score"].Value.Replace(',', '.');
                if (!decimal.TryParse(
                        scoreText,
                        NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture,
                        out decimal score)
                    || score < 0
                    || score > 5)
                {
                    continue;
                }

                string key = match.Groups["label"].Value.Equals("Presse", StringComparison.OrdinalIgnoreCase)
                    ? "presse"
                    : "public";
                ratings[key] = score.ToString("0.##", CultureInfo.InvariantCulture);
            }

            return ratings.Count == 0 ? null : ratings;
        }

        [GeneratedRegex(
            "rating-item-content(?:(?!rating-item-content)[\\s\\S]){0,800}?rating-title[^>]*>\\s*(?<label>Presse|Spectateurs)\\s*</span>(?:(?!rating-item-content)[\\s\\S]){0,1000}?stareval-note[^>]*>\\s*(?<score>[0-5](?:[,.][0-9]+)?)\\s*</span>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex RatingRegex();
    }
}
