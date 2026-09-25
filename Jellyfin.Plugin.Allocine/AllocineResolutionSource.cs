namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// How an AlloCiné identifier was obtained.
    /// </summary>
    public enum AllocineResolutionSource
    {
        /// <summary>
        /// No identifier was selected.
        /// </summary>
        None = 0,

        /// <summary>
        /// The identifier was jointly proven from IMDb and/or TMDb.
        /// </summary>
        ExactIdentifiers = 1,

        /// <summary>
        /// The identifier came from an unambiguous title and year fallback.
        /// </summary>
        TitleYear = 2,

        /// <summary>
        /// The identifier was persisted before exact versus title/year provenance was recorded.
        /// </summary>
        Unknown = 3,

        /// <summary>
        /// An exact IMDb/TMDb resolve was attempted and produced no unique identifier.
        /// </summary>
        ExactMiss = 4
    }
}
