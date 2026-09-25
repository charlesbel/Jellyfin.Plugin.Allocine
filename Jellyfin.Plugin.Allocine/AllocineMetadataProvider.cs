using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Post-refresh enricher that may persist a proven AlloCiné identifier and refresh the private rating cache.
    /// </summary>
    public sealed class AllocineMetadataProvider : ICustomMetadataProvider<Movie>, ICustomMetadataProvider<Series>
    {
        private readonly AllocineRatingCacheService _cache;
        private readonly AllocineRatingStore _store;
        private readonly ILogger<AllocineMetadataProvider> _logger;
        private readonly PluginConfiguration? _configuration;

        /// <summary>
        /// Initializes a new instance of the <see cref="AllocineMetadataProvider"/> class.
        /// </summary>
        /// <param name="cache">The coalescing rating cache shared with the scheduled task.</param>
        /// <param name="mapping">The exact identity resolver used by the cache.</param>
        /// <param name="store">The private plugin store used for provenance.</param>
        /// <param name="logger">The logger.</param>
        public AllocineMetadataProvider(
            AllocineRatingCacheService cache,
            IAllocineMappingProvider mapping,
            AllocineRatingStore store,
            ILogger<AllocineMetadataProvider> logger)
            : this(cache, mapping, store, logger, null)
        {
        }

        internal AllocineMetadataProvider(
            AllocineRatingCacheService cache,
            IAllocineMappingProvider mapping,
            AllocineRatingStore store,
            ILogger<AllocineMetadataProvider> logger,
            PluginConfiguration? configuration)
        {
            ArgumentNullException.ThrowIfNull(mapping);
            _cache = cache;
            _store = store;
            _logger = logger;
            _configuration = configuration;
        }

        /// <inheritdoc />
        public string Name => AllocineProviderNames.ProviderName;

        internal AllocineRatingCacheService Cache => _cache;

        internal AllocineRatingStore Store => _store;

        private PluginConfiguration Configuration =>
            _configuration ?? Plugin.Instance?.Configuration ?? new PluginConfiguration();

        /// <inheritdoc />
        public Task<ItemUpdateType> FetchAsync(
            Movie item,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken)
        {
            return FetchCoreAsync(item, cancellationToken);
        }

        /// <inheritdoc />
        public Task<ItemUpdateType> FetchAsync(
            Series item,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken)
        {
            return FetchCoreAsync(item, cancellationToken);
        }

        private async Task<ItemUpdateType> FetchCoreAsync(BaseItem item, CancellationToken cancellationToken)
        {
            if (item.IsLocked
                || !AllocineRefreshTask.TryCreateRequest(item, out AllocineRatingsRequest request))
            {
                return ItemUpdateType.None;
            }

            bool nativeChanged = false;
            if (Configuration.WriteNativeAllocineIds)
            {
                try
                {
                    string? exactId = await _cache.GetExactAllocineIdAsync(request, cancellationToken).ConfigureAwait(false);
                    if (exactId != null)
                    {
                        nativeChanged = await _cache
                            .TryWriteNativeIdAsync(item, request, exactId, cancellationToken)
                            .ConfigureAwait(false);
                        string? current = item.GetProviderId(AllocineProviderNames.Key);
                        if (AllocineProviderNames.IsValidId(current))
                        {
                            request = request with { AllocineId = current };
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Allocine] Exact identity resolution failed for item {ItemId}", item.Id);
                }
            }

            try
            {
                await _cache.RefreshIfNeededAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Allocine] Private rating refresh failed for item {ItemId}", item.Id);
            }

            return nativeChanged ? ItemUpdateType.MetadataImport : ItemUpdateType.None;
        }
    }
}
