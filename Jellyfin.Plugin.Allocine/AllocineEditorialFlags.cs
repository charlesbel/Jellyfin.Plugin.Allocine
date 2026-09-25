using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Editorial AlloCiné pills shown next to press and spectator scores.
    /// </summary>
    internal static class AllocineEditorialFlags
    {
        /// <summary>
        /// Cache and JSON key for the Classiques pill.
        /// </summary>
        public const string Classiques = "classiques";

        /// <summary>
        /// Cache and JSON key for the Le club Aime pill.
        /// </summary>
        public const string ClubAime = "clubAime";

        /// <summary>
        /// Stored value when AlloCiné awarded the pill.
        /// </summary>
        public const string Present = "1";

        /// <summary>
        /// Stored value when the pill was fetched and is absent.
        /// </summary>
        public const string Absent = "0";

        /// <summary>
        /// Writes both editorial keys so later reads can tell a fetch already ran.
        /// </summary>
        /// <param name="ratings">The rating dictionary to update.</param>
        /// <param name="classiques">Whether the Classiques pill is present.</param>
        /// <param name="clubAime">Whether the Le club Aime pill is present.</param>
        public static void Apply(Dictionary<string, string> ratings, bool classiques, bool clubAime)
        {
            ArgumentNullException.ThrowIfNull(ratings);
            ratings[Classiques] = classiques ? Present : Absent;
            ratings[ClubAime] = clubAime ? Present : Absent;
        }

        /// <summary>
        /// Returns whether both editorial keys were persisted.
        /// </summary>
        /// <param name="ratings">The cached ratings.</param>
        /// <returns><see langword="true"/> when both keys exist.</returns>
        public static bool HasKeys(IReadOnlyDictionary<string, string>? ratings)
        {
            return ratings != null
                && ratings.ContainsKey(Classiques)
                && ratings.ContainsKey(ClubAime);
        }

        /// <summary>
        /// Copies ratings and fills missing editorial keys with <see cref="Absent"/>.
        /// </summary>
        /// <param name="ratings">The source ratings.</param>
        /// <returns>A new dictionary that always contains both editorial keys.</returns>
        public static Dictionary<string, string> WithDefaults(IDictionary<string, string> ratings)
        {
            ArgumentNullException.ThrowIfNull(ratings);
            var copy = new Dictionary<string, string>(ratings, StringComparer.Ordinal);
            copy.TryAdd(Classiques, Absent);
            copy.TryAdd(ClubAime, Absent);
            return copy;
        }
    }
}
