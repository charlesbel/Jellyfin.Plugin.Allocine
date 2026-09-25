using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Exposes a clickable AlloCiné link from a stored native identifier.
    /// </summary>
    public sealed class AllocineExternalUrlProvider : IExternalUrlProvider
    {
        /// <inheritdoc />
        public string Name => AllocineProviderNames.ProviderName;

        /// <inheritdoc />
        public IEnumerable<string> GetExternalUrls(BaseItem item)
        {
            if (!item.TryGetProviderId(AllocineProviderNames.Key, out string? allocineId)
                || !AllocineProviderNames.IsValidId(allocineId))
            {
                yield break;
            }

            if (item is Movie)
            {
                yield return AllocineProviderNames.MovieUrl(allocineId);
            }
            else if (item is Series)
            {
                yield return AllocineProviderNames.SeriesUrl(allocineId);
            }
        }
    }
}
