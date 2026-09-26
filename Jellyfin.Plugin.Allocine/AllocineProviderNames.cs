using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Stable AlloCiné provider identity and public URL helpers.
    /// </summary>
    public static class AllocineProviderNames
    {
        /// <summary>
        /// The immutable ASCII provider-id key persisted by Jellyfin.
        /// </summary>
        public const string Key = "Allocine";

        /// <summary>
        /// The display name shown in Jellyfin's metadata editor and related links.
        /// </summary>
        public const string ProviderName = "AlloCiné";

        /// <summary>
        /// Returns whether <paramref name="allocineId"/> is a canonical numeric AlloCiné identifier.
        /// </summary>
        /// <param name="allocineId">The candidate identifier.</param>
        /// <returns><see langword="true"/> when the identifier is safe to persist or turn into a URL.</returns>
        public static bool IsValidId([NotNullWhen(true)] string? allocineId)
        {
            if (string.IsNullOrWhiteSpace(allocineId)
                || allocineId.Length > 12
                || allocineId[0] < '1'
                || allocineId[0] > '9')
            {
                return false;
            }

            foreach (char character in allocineId)
            {
                if (character < '0' || character > '9')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Builds the canonical public movie URL for a validated AlloCiné identifier.
        /// </summary>
        /// <param name="allocineId">The validated numeric identifier.</param>
        /// <returns>The public movie URL.</returns>
        public static string MovieUrl(string allocineId)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "https://www.allocine.fr/film/fichefilm_gen_cfilm={0}.html",
                allocineId);
        }

        /// <summary>
        /// Builds the canonical public series URL for a validated AlloCiné identifier.
        /// </summary>
        /// <param name="allocineId">The validated numeric identifier.</param>
        /// <returns>The public series URL.</returns>
        public static string SeriesUrl(string allocineId)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "https://www.allocine.fr/series/ficheserie_gen_cserie={0}.html",
                allocineId);
        }
    }
}
