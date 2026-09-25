using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Separates stable AlloCiné identity resolution from volatile rating retrieval.
    /// </summary>
    public interface IAllocineMappingProvider : IAllocineRatingProvider
    {
        /// <summary>
        /// Resolves one exact media identity to an AlloCiné identifier.
        /// </summary>
        /// <param name="request">The exact media identity.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The exact AlloCiné identifier, or <see langword="null"/>.</returns>
        Task<string?> ResolveAllocineIdAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken);

        /// <summary>
        /// Resolves one media identity and reports whether the match is exact or a title/year fallback.
        /// </summary>
        /// <param name="request">The exact media identity.</param>
        /// <param name="allowTitleYearFallback">
        /// Whether an unambiguous title and year match may be used when IMDb/TMDb resolution is unavailable.
        /// </param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolution result.</returns>
        Task<AllocineResolvedIdentity> ResolveIdentityAsync(
            AllocineRatingsRequest request,
            bool allowTitleYearFallback,
            CancellationToken cancellationToken);

        /// <summary>
        /// Fetches current ratings for a previously validated AlloCiné identifier.
        /// </summary>
        /// <param name="allocineId">The validated AlloCiné identifier.</param>
        /// <param name="mediaType">Movie or Series.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The ratings, or <see langword="null"/>.</returns>
        Task<Dictionary<string, string>?> GetRatingsByAllocineIdAsync(
            string allocineId,
            string mediaType,
            CancellationToken cancellationToken);
    }
}
