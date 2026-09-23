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
