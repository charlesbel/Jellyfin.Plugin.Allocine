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
        /// Cache and JSON key for the Les Indés pill.
        /// </summary>
        public const string LesIndes = "lesIndes";

        /// <summary>
        /// Cache and JSON key for the Club Scream pill.
        /// </summary>
        public const string ClubScream = "clubScream";

        /// <summary>
        /// Stored value when AlloCiné awarded the pill.
        /// </summary>
        public const string Present = "1";

        /// <summary>
        /// Stored value when the pill was fetched and is absent.
        /// </summary>
        public const string Absent = "0";

        private static readonly string[] AllKeys = [Classiques, ClubAime, LesIndes, ClubScream];

        /// <summary>
        /// Writes every editorial key so later reads can tell a fetch already ran.
        /// </summary>
        /// <param name="ratings">The rating dictionary to update.</param>
        /// <param name="classiques">Whether the Classiques pill is present.</param>
        /// <param name="clubAime">Whether the Le club Aime pill is present.</param>
        /// <param name="lesIndes">Whether the Les Indés pill is present.</param>
        /// <param name="clubScream">Whether the Club Scream pill is present.</param>
        public static void Apply(
            Dictionary<string, string> ratings,
            bool classiques,
            bool clubAime,
            bool lesIndes,
            bool clubScream)
        {
            ArgumentNullException.ThrowIfNull(ratings);
            ratings[Classiques] = classiques ? Present : Absent;
            ratings[ClubAime] = clubAime ? Present : Absent;
            ratings[LesIndes] = lesIndes ? Present : Absent;
            ratings[ClubScream] = clubScream ? Present : Absent;
        }

        /// <summary>
        /// Returns whether every editorial key was persisted.
        /// </summary>
        /// <param name="ratings">The cached ratings.</param>
        /// <returns><see langword="true"/> when all keys exist.</returns>
        public static bool HasKeys(IReadOnlyDictionary<string, string>? ratings)
        {
            if (ratings == null)
            {
                return false;
            }

            foreach (string key in AllKeys)
            {
                if (!ratings.ContainsKey(key))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Copies ratings and fills missing editorial keys with <see cref="Absent"/>.
        /// </summary>
        /// <param name="ratings">The source ratings.</param>
        /// <returns>A new dictionary that always contains every editorial key.</returns>
        public static Dictionary<string, string> WithDefaults(IDictionary<string, string> ratings)
        {
            ArgumentNullException.ThrowIfNull(ratings);
            var copy = new Dictionary<string, string>(ratings, StringComparer.Ordinal);
            foreach (string key in AllKeys)
            {
                copy.TryAdd(key, Absent);
            }

            return copy;
        }
    }
}
