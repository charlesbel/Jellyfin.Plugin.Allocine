using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Declares the native AlloCiné identifier field for movies.
    /// </summary>
    public sealed class AllocineMovieExternalId : IExternalId
    {
        /// <inheritdoc />
        public string ProviderName => AllocineProviderNames.ProviderName;

        /// <inheritdoc />
        public string Key => AllocineProviderNames.Key;

        /// <inheritdoc />
        public ExternalIdMediaType? Type => ExternalIdMediaType.Movie;

        /// <inheritdoc />
        public bool Supports(IHasProviderIds item) => item is Movie;
    }
}
