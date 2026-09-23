using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Warms and refreshes the private AlloCiné rating database.
    /// </summary>
    public sealed class AllocineRefreshTask : IScheduledTask
    {
        private readonly TimeSpan _requestPacing;
        private readonly ILibraryManager _libraryManager;
        private readonly AllocineRatingCacheService _cacheService;
        private readonly ILogger<AllocineRefreshTask> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AllocineRefreshTask"/> class.
        /// </summary>
        /// <param name="libraryManager">Jellyfin's supported library abstraction.</param>
        /// <param name="cacheService">The cache-first rating service.</param>
        /// <param name="logger">The logger.</param>
        public AllocineRefreshTask(
            ILibraryManager libraryManager,
            AllocineRatingCacheService cacheService,
            ILogger<AllocineRefreshTask> logger)
            : this(libraryManager, cacheService, logger, TimeSpan.FromSeconds(1))
        {
        }

        internal AllocineRefreshTask(
            ILibraryManager libraryManager,
            AllocineRatingCacheService cacheService,
            ILogger<AllocineRefreshTask> logger,
            TimeSpan requestPacing)
        {
            _libraryManager = libraryManager;
            _cacheService = cacheService;
            _logger = logger;
            _requestPacing = requestPacing;
        }

        /// <inheritdoc />
        public string Name => "Actualiser les notes AlloCiné";

        /// <inheritdoc />
        public string Key => "AllocineRatingsRefresh";

        /// <inheritdoc />
        public string Description => "Récupère les notes AlloCiné des films et séries dans la base privée du plugin, sans modifier les notes Jellyfin.";

        /// <inheritdoc />
        public string Category => "Allociné Ratings";

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => DefaultTriggers();

        internal static IEnumerable<TaskTriggerInfo> DefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
            };
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            List<BaseItem> items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                IsVirtualItem = false,
                Recursive = true,
            }).ToList();

            int completed = 0;
            int refreshed = 0;
            int failed = 0;
            int skipped = 0;
            foreach (BaseItem item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryCreateScheduledRequest(item, out AllocineRatingsRequest request))
                {
                    skipped++;
                    ReportProgress(progress, ++completed, items.Count);
                    continue;
                }

                try
                {
                    if (await _cacheService.NeedsRefreshAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        AllocineRefreshOutcome outcome = await _cacheService
                            .RefreshIfNeededAsync(request, cancellationToken)
                            .ConfigureAwait(false);
                        if (outcome.Result == AllocineRefreshResult.Updated)
                        {
                            refreshed++;
                        }
                        else if (outcome.Result == AllocineRefreshResult.Failed)
                        {
                            failed++;
                        }
                        else
                        {
                            skipped++;
                        }

                        await Task.Delay(_requestPacing, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        skipped++;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.LogWarning(ex, "[Allocine] Scheduled refresh failed for item {ItemId}; continuing.", item.Id);
                    }
                }

                ReportProgress(progress, ++completed, items.Count);
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "[Allocine] Scheduled refresh complete: {Refreshed} refreshed, {Failed} failed/backed off, {Skipped} skipped, {Total} examined.",
                    refreshed,
                    failed,
                    skipped,
                    items.Count);
            }

            progress.Report(100);
        }

        internal static bool TryCreateRequest(BaseItem item, out AllocineRatingsRequest request)
        {
            string? mediaType = item switch
            {
                Movie => BaseItemKind.Movie.ToString(),
                Series => BaseItemKind.Series.ToString(),
                _ => null,
            };
            if (mediaType == null
                || string.IsNullOrWhiteSpace(item.Name)
                || item.ProductionYear is not int year)
            {
                request = null!;
                return false;
            }

            request = new AllocineRatingsRequest(
                item.Id.ToString("N"),
                mediaType,
                item.Name,
                item.OriginalTitle,
                year,
                item.GetProviderId(MetadataProvider.Imdb),
                item.GetProviderId(MetadataProvider.Tmdb));
            return true;
        }

        internal static bool TryCreateScheduledRequest(BaseItem item, out AllocineRatingsRequest request)
        {
            if (!TryCreateRequest(item, out request))
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(request.ImdbId)
                || !string.IsNullOrWhiteSpace(request.TmdbId);
        }

        private static void ReportProgress(IProgress<double> progress, int completed, int total)
        {
            progress.Report(total == 0 ? 100 : (double)completed / total * 100);
        }
    }
}
