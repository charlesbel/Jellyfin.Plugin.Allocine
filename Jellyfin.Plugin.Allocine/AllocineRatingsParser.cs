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

            bool classiques = ContainsEditorialMarker(html, "button-classiques-gold", "gelule_classiqueor");
            bool clubAime = ContainsEditorialMarker(html, "button-club-300", "gelule_clubaime");
            bool lesIndes = ContainsEditorialMarker(html, "button-les-indes", "gelule_lesindes");
            bool clubScream = ContainsEditorialMarker(html, "button-club-scream", "gelule_clubscream");
            if (ratings.Count == 0 && !classiques && !clubAime && !lesIndes && !clubScream)
            {
                return null;
            }

            AllocineEditorialFlags.Apply(ratings, classiques, clubAime, lesIndes, clubScream);
            return ratings;
        }

        private static bool ContainsEditorialMarker(string html, string className, string trackingName)
        {
            return ClassAttributeContains(html, className)
                || TrackingAttributeContains(html, trackingName);
        }

        private static bool ClassAttributeContains(string html, string className)
        {
            return Regex.IsMatch(
                html,
                @"class\s*=\s*([""'])[^""']*\b" + Regex.Escape(className) + @"\b[^""']*\1",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool TrackingAttributeContains(string html, string trackingName)
        {
            string escaped = Regex.Escape(trackingName);
            return Regex.IsMatch(
                html,
                @"data-allocine-tracking-position-name\s*=\s*([""'])" + escaped + @"\1",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || Regex.IsMatch(
                html,
                @"(?:&quot;|"")position_name(?:&quot;|"")\s*:\s*(?:&quot;|"")" + escaped + @"(?:&quot;|"")",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        [GeneratedRegex(
            "rating-item-content(?:(?!rating-item-content)[\\s\\S]){0,800}?rating-title[^>]*>\\s*(?<label>Presse|Spectateurs)\\s*</span>(?:(?!rating-item-content)[\\s\\S]){0,1000}?stareval-note[^>]*>\\s*(?<score>[0-5](?:[,.][0-9]+)?)\\s*</span>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex RatingRegex();
    }
}
