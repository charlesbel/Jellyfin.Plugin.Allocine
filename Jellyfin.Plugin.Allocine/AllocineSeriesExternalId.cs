using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Declares the native AlloCiné identifier field for series.
    /// </summary>
    public sealed class AllocineSeriesExternalId : IExternalId
    {
        /// <inheritdoc />
        public string ProviderName => AllocineProviderNames.ProviderName;

        /// <inheritdoc />
        public string Key => AllocineProviderNames.Key;

        /// <inheritdoc />
        public ExternalIdMediaType? Type => ExternalIdMediaType.Series;

        /// <inheritdoc />
        public bool Supports(IHasProviderIds item) => item is Series;
    }
}
