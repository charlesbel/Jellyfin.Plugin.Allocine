using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Serves cached ratings immediately and coalesces due remote refreshes.
    /// </summary>
    public sealed class AllocineRatingCacheService
    {
        private readonly AllocineRatingStore _store;
        private readonly IAllocineRatingProvider _provider;
        private readonly ILogger<AllocineRatingCacheService> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _inFlight = new(StringComparer.Ordinal);

        /// <summary>
        /// Initializes a new instance of the <see cref="AllocineRatingCacheService"/> class.
        /// </summary>
        /// <param name="store">The private plugin store.</param>
        /// <param name="provider">The remote rating provider.</param>
        /// <param name="logger">The logger.</param>
        public AllocineRatingCacheService(
            AllocineRatingStore store,
            IAllocineRatingProvider provider,
            ILogger<AllocineRatingCacheService> logger)
            : this(store, provider, logger, TimeProvider.System)
        {
        }

        internal AllocineRatingCacheService(
            AllocineRatingStore store,
            IAllocineRatingProvider provider,
            ILogger<AllocineRatingCacheService> logger,
            TimeProvider timeProvider)
        {
            _store = store;
            _provider = provider;
            _logger = logger;
            _timeProvider = timeProvider;
        }

        /// <summary>
        /// Returns any valid cached rating immediately. A remote lookup is performed only when no
        /// displayable rating exists and the retry backoff permits it.
        /// </summary>
        /// <param name="request">The exact media identity.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The cached or fetched ratings, or <see langword="null"/>.</returns>
        public async Task<Dictionary<string, string>?> GetRatingsAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            AllocineCacheEntry? cached = await TryReadAsync(request, cancellationToken).ConfigureAwait(false);
            if (HasValidRatings(cached, request))
            {
                return CopyRatings(cached);
            }

            AllocineRefreshOutcome outcome = await RefreshIfNeededAsync(request, cancellationToken).ConfigureAwait(false);
            return outcome.Ratings;
        }

        /// <summary>
        /// Determines whether a scheduled refresh is due without making a network call.
        /// </summary>
        /// <param name="request">The exact media identity.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><see langword="true"/> when a retry is due.</returns>
        public async Task<bool> NeedsRefreshAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            AllocineCacheEntry? cached = await TryReadAsync(request, cancellationToken).ConfigureAwait(false);
            return !IsFresh(cached, request) && IsRetryDue(cached, request);
        }

        internal async Task<AllocineRefreshOutcome> RefreshIfNeededAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            string key = AllocineRatingStore.CacheKey(request);
            SemaphoreSlim gate = _inFlight.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AllocineCacheEntry? cached = await TryReadAsync(request, cancellationToken).ConfigureAwait(false);
                if (IsFresh(cached, request) || !IsRetryDue(cached, request))
                {
                    return new AllocineRefreshOutcome(AllocineRefreshResult.Skipped, CopyRatings(cached));
                }

                Dictionary<string, string>? fetched;
                try
                {
                    fetched = await FetchRatingsAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Allocine] Remote rating refresh failed for Jellyfin item {ItemId}", request.ItemId);
                    await TryRecordFailureAsync(request, cancellationToken).ConfigureAwait(false);
                    return new AllocineRefreshOutcome(AllocineRefreshResult.Failed, CopyRatings(cached));
                }

                if (fetched is not { Count: > 0 })
                {
                    await TryRecordFailureAsync(request, cancellationToken).ConfigureAwait(false);
                    return new AllocineRefreshOutcome(AllocineRefreshResult.Failed, CopyRatings(cached));
                }

                try
                {
                    await _store.WriteAsync(request, fetched, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                    return new AllocineRefreshOutcome(AllocineRefreshResult.Updated, fetched);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Allocine] Failed to persist ratings for Jellyfin item {ItemId}", request.ItemId);
                    return new AllocineRefreshOutcome(AllocineRefreshResult.Failed, fetched);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        internal static TimeSpan FreshnessFor(AllocineRatingsRequest request, DateTimeOffset now)
        {
            int ageInYears = now.Year - request.Year;
            if (ageInYears <= 0)
            {
                return TimeSpan.FromDays(1);
            }

            if (ageInYears == 1)
            {
                return TimeSpan.FromDays(3);
            }

            if (request.MediaType.Equals("Series", StringComparison.OrdinalIgnoreCase))
            {
                return TimeSpan.FromDays(14);
            }

            return ageInYears <= 4 ? TimeSpan.FromDays(30) : TimeSpan.FromDays(90);
        }

        private static Dictionary<string, string>? CopyRatings(AllocineCacheEntry? entry)
        {
            return entry is { Found: true, Ratings: not null }
                ? new Dictionary<string, string>(entry.Ratings, StringComparer.Ordinal)
                : null;
        }

        private static bool IdentityMatches(AllocineCacheEntry? entry, AllocineRatingsRequest request)
        {
            return entry != null
                && string.Equals(entry.IdentityKey, AllocineRatingStore.IdentityKey(request), StringComparison.Ordinal);
        }

        private static bool HasValidRatings(AllocineCacheEntry? entry, AllocineRatingsRequest request)
        {
            return IdentityMatches(entry, request)
                && entry is { Found: true, Ratings.Count: > 0 };
        }

        private bool IsFresh(AllocineCacheEntry? entry, AllocineRatingsRequest request)
        {
            if (!HasValidRatings(entry, request))
            {
                return false;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            TimeSpan age = now - entry!.FetchedAt;
            return age >= TimeSpan.Zero && age <= FreshnessFor(request, now);
        }

        private bool IsRetryDue(AllocineCacheEntry? entry, AllocineRatingsRequest request)
        {
            if (!IdentityMatches(entry, request)
                || entry!.ConsecutiveFailures <= 0
                || entry.LastAttemptAt == null)
            {
                return true;
            }

            TimeSpan backoff = entry.ConsecutiveFailures switch
            {
                1 => TimeSpan.FromHours(6),
                2 => TimeSpan.FromDays(1),
                3 => TimeSpan.FromDays(3),
                4 => TimeSpan.FromDays(7),
                _ => TimeSpan.FromDays(14),
            };
            TimeSpan elapsed = _timeProvider.GetUtcNow() - entry.LastAttemptAt.Value;
            return elapsed >= backoff;
        }

        private async Task<Dictionary<string, string>?> FetchRatingsAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            if (_provider is not IAllocineMappingProvider mappingProvider)
            {
                return await _provider.GetRatingsAsync(request, cancellationToken).ConfigureAwait(false);
            }

            string? allocineId = null;
            try
            {
                AllocineMappingEntry? mapping = await _store.ReadMappingAsync(request, cancellationToken).ConfigureAwait(false);
                if (IsFreshMapping(mapping, request))
                {
                    allocineId = mapping!.AllocineId;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Allocine] Failed to read the private identity mapping cache for item {ItemId}", request.ItemId);
            }

            if (allocineId == null)
            {
                allocineId = await mappingProvider.ResolveAllocineIdAsync(request, cancellationToken).ConfigureAwait(false);
                if (!IsValidAllocineId(allocineId))
                {
                    return null;
                }

                try
                {
                    await _store.WriteMappingAsync(
                        request,
                        allocineId!,
                        _timeProvider.GetUtcNow(),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Allocine] Failed to persist identity mapping for item {ItemId}", request.ItemId);
                }
            }

            return await mappingProvider
                .GetRatingsByAllocineIdAsync(allocineId!, request.MediaType, cancellationToken)
                .ConfigureAwait(false);
        }

        private bool IsFreshMapping(AllocineMappingEntry? mapping, AllocineRatingsRequest request)
        {
            if (mapping == null
                || !string.Equals(mapping.IdentityKey, AllocineRatingStore.IdentityKey(request), StringComparison.Ordinal)
                || !IsValidAllocineId(mapping.AllocineId))
            {
                return false;
            }

            TimeSpan lifetime = string.IsNullOrWhiteSpace(request.ImdbId) && string.IsNullOrWhiteSpace(request.TmdbId)
                ? TimeSpan.FromDays(30)
                : TimeSpan.FromDays(180);
            TimeSpan age = _timeProvider.GetUtcNow() - mapping.ResolvedAt;
            return age >= TimeSpan.Zero && age <= lifetime;
        }

        private static bool IsValidAllocineId(string? allocineId)
        {
            if (string.IsNullOrWhiteSpace(allocineId)
                || allocineId.Length > 12
                || allocineId[0] < '1'
                || allocineId[0] > '9')
            {
                return false;
            }

            foreach (char character in allocineId)
            {
                if (character < '0' || character > '9')
                {
                    return false;
                }
            }

            return true;
        }

        private async Task<AllocineCacheEntry?> TryReadAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _store.ReadAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Allocine] Failed to read the private rating cache for item {ItemId}", request.ItemId);
                return null;
            }
        }

        private async Task TryRecordFailureAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                await _store.RecordFailureAsync(request, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Allocine] Failed to persist refresh backoff for Jellyfin item {ItemId}", request.ItemId);
            }
        }
    }
}
