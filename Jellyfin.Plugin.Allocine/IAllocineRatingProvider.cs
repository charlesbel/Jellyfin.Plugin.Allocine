using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Fetches ratings from the remote AlloCiné identity and rating pipeline.
    /// </summary>
    public interface IAllocineRatingProvider
    {
        /// <summary>
        /// Fetches ratings for one exact media identity.
        /// </summary>
        /// <param name="request">The media identity.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The ratings, or <see langword="null"/> when no safe match exists.</returns>
        Task<Dictionary<string, string>?> GetRatingsAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken);
    }
}
