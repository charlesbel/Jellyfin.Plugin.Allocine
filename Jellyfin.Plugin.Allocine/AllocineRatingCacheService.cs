using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
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
                string? resolvedAllocineId = request.AllocineId;
                try
                {
                    (fetched, resolvedAllocineId) = await FetchRatingsAsync(request, cancellationToken).ConfigureAwait(false);
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
                    AllocineRatingsRequest persisted = request with
                    {
                        AllocineId = resolvedAllocineId ?? request.AllocineId,
                    };
                    await _store.WriteAsync(persisted, fetched, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
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
            if (entry == null
                || !string.Equals(entry.IdentityKey, AllocineRatingStore.IdentityKey(request), StringComparison.Ordinal))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(request.AllocineId)
                || string.IsNullOrWhiteSpace(entry.AllocineId)
                || string.Equals(request.AllocineId, entry.AllocineId, StringComparison.Ordinal);
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

        internal async Task<string?> GetExactAllocineIdAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.ImdbId) && string.IsNullOrWhiteSpace(request.TmdbId))
            {
                return null;
            }

            string key = AllocineRatingStore.CacheKey(request);
            SemaphoreSlim gate = _inFlight.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AllocineMappingEntry? mapping = await TryReadMappingAsync(request, cancellationToken).ConfigureAwait(false);
                if (IsFreshExactMapping(mapping, request))
                {
                    return mapping!.AllocineId;
                }

                if (mapping != null
                    && IsFreshMapping(mapping, request)
                    && mapping.Source != AllocineResolutionSource.Unknown)
                {
                    return null;
                }

                if (_provider is not IAllocineMappingProvider mappingProvider)
                {
                    return null;
                }

                AllocineResolvedIdentity resolved = await mappingProvider
                    .ResolveIdentityAsync(request, allowTitleYearFallback: false, cancellationToken)
                    .ConfigureAwait(false);
                if (resolved.IsTransient)
                {
                    return mapping is { Source: AllocineResolutionSource.ExactIdentifiers }
                        && AllocineProviderNames.IsValidId(mapping.AllocineId)
                        ? mapping.AllocineId
                        : null;
                }

                if (resolved.IsConflict
                    || resolved.Source != AllocineResolutionSource.ExactIdentifiers
                    || !AllocineProviderNames.IsValidId(resolved.AllocineId))
                {
                    if (mapping is { Source: AllocineResolutionSource.ExactIdentifiers }
                        && AllocineProviderNames.IsValidId(mapping.AllocineId))
                    {
                        await TryPreserveExactMappingAsync(request, mapping, cancellationToken).ConfigureAwait(false);
                        return mapping.AllocineId;
                    }

                    await TryRememberExactMissAsync(request, mapping, cancellationToken).ConfigureAwait(false);
                    return null;
                }

                try
                {
                    await _store.WriteMappingAsync(
                        request,
                        resolved.AllocineId!,
                        _timeProvider.GetUtcNow(),
                        AllocineResolutionSource.ExactIdentifiers,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Allocine] Failed to persist exact identity mapping for item {ItemId}", request.ItemId);
                }

                return resolved.AllocineId;
            }
            finally
            {
                gate.Release();
            }
        }

        internal async Task<bool> TryWriteNativeIdAsync(
            BaseItem item,
            AllocineRatingsRequest request,
            string exactId,
            CancellationToken cancellationToken,
            bool recordProvenance = true)
        {
            string? current = item.GetProviderId(AllocineProviderNames.Key);
            if (string.Equals(current, exactId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(current))
            {
                AllocineNativeWriteEntry? provenance = await _store
                    .ReadNativeWriteAsync(request.ItemId, cancellationToken)
                    .ConfigureAwait(false);
                if (provenance == null
                    || !string.Equals(provenance.AllocineId, current, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (!item.TrySetProviderId(AllocineProviderNames.Key, exactId))
            {
                return false;
            }

            if (recordProvenance)
            {
                await RecordNativeWriteAsync(request, exactId, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }

        internal Task RecordNativeWriteAsync(
            AllocineRatingsRequest request,
            string allocineId,
            CancellationToken cancellationToken)
        {
            return _store.RecordNativeWriteAsync(
                request.ItemId,
                allocineId,
                AllocineRatingStore.IdentityKey(request),
                _timeProvider.GetUtcNow(),
                cancellationToken);
        }

        private async Task TryPreserveExactMappingAsync(
            AllocineRatingsRequest request,
            AllocineMappingEntry mapping,
            CancellationToken cancellationToken)
        {
            try
            {
                await _store.WriteMappingAsync(
                    request,
                    mapping.AllocineId,
                    _timeProvider.GetUtcNow(),
                    AllocineResolutionSource.ExactIdentifiers,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Allocine] Failed to refresh exact identity mapping for item {ItemId}", request.ItemId);
            }
        }

        private async Task TryRememberExactMissAsync(
            AllocineRatingsRequest request,
            AllocineMappingEntry? mapping,
            CancellationToken cancellationToken)
        {
            if (mapping == null
                || mapping.Source == AllocineResolutionSource.ExactIdentifiers
                || !AllocineProviderNames.IsValidId(mapping.AllocineId))
            {
                return;
            }

            try
            {
                await _store.WriteMappingAsync(
                    request,
                    mapping.AllocineId,
                    _timeProvider.GetUtcNow(),
                    AllocineResolutionSource.ExactMiss,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Allocine] Failed to persist exact-miss mapping for item {ItemId}", request.ItemId);
            }
        }

        private async Task<(Dictionary<string, string>? Ratings, string? AllocineId)> FetchRatingsAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            if (_provider is not IAllocineMappingProvider mappingProvider)
            {
                Dictionary<string, string>? ratings = await _provider
                    .GetRatingsAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                return (ratings, request.AllocineId);
            }

            string? allocineId = null;
            AllocineResolutionSource source = AllocineResolutionSource.None;
            AllocineMappingEntry? mapping = null;
            if (await IsUserOwnedNativeIdAsync(request, cancellationToken).ConfigureAwait(false))
            {
                allocineId = request.AllocineId;
                source = AllocineResolutionSource.ExactIdentifiers;
            }
            else
            {
                mapping = await TryReadMappingAsync(request, cancellationToken).ConfigureAwait(false);
                if (IsFreshMapping(mapping, request))
                {
                    allocineId = mapping!.AllocineId;
                    source = mapping.Source;
                }
            }

            if (allocineId == null)
            {
                bool preserveExact = mapping is { Source: AllocineResolutionSource.ExactIdentifiers }
                    && AllocineProviderNames.IsValidId(mapping.AllocineId);
                AllocineResolvedIdentity resolved = await ResolveRatingsIdentityAsync(
                    mappingProvider,
                    request,
                    allowTitleYearFallback: !preserveExact,
                    cancellationToken).ConfigureAwait(false);
                if (resolved.IsTransient)
                {
                    return (null, null);
                }

                if (preserveExact
                    && (resolved.Source != AllocineResolutionSource.ExactIdentifiers
                        || !AllocineProviderNames.IsValidId(resolved.AllocineId)))
                {
                    await TryPreserveExactMappingAsync(request, mapping!, cancellationToken).ConfigureAwait(false);
                    allocineId = mapping!.AllocineId;
                    source = AllocineResolutionSource.ExactIdentifiers;
                }
                else if (resolved.IsConflict || !AllocineProviderNames.IsValidId(resolved.AllocineId))
                {
                    return (null, null);
                }
                else
                {
                    allocineId = resolved.AllocineId;
                    source = resolved.Source;
                    try
                    {
                        await _store.WriteMappingAsync(
                            request,
                            allocineId!,
                            _timeProvider.GetUtcNow(),
                            source,
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
            }

            Dictionary<string, string>? fetched = await mappingProvider
                .GetRatingsByAllocineIdAsync(allocineId!, request.MediaType, cancellationToken)
                .ConfigureAwait(false);
            return (fetched, allocineId);
        }

        private static async Task<AllocineResolvedIdentity> ResolveRatingsIdentityAsync(
            IAllocineMappingProvider mappingProvider,
            AllocineRatingsRequest request,
            bool allowTitleYearFallback,
            CancellationToken cancellationToken)
        {
            bool hasStableIds = !string.IsNullOrWhiteSpace(request.ImdbId) || !string.IsNullOrWhiteSpace(request.TmdbId);
            if (hasStableIds)
            {
                AllocineResolvedIdentity exact = await mappingProvider
                    .ResolveIdentityAsync(request, allowTitleYearFallback: false, cancellationToken)
                    .ConfigureAwait(false);
                if (exact.IsTransient || exact.IsConflict || AllocineProviderNames.IsValidId(exact.AllocineId) || !allowTitleYearFallback)
                {
                    return exact;
                }
            }

            return await mappingProvider
                .ResolveIdentityAsync(request, allowTitleYearFallback: true, cancellationToken)
                .ConfigureAwait(false);
        }

        private bool IsFreshExactMapping(AllocineMappingEntry? mapping, AllocineRatingsRequest request)
        {
            return IsFreshMapping(mapping, request)
                && mapping!.Source == AllocineResolutionSource.ExactIdentifiers;
        }

        private bool IsFreshMapping(AllocineMappingEntry? mapping, AllocineRatingsRequest request)
        {
            if (mapping == null
                || !string.Equals(mapping.IdentityKey, AllocineRatingStore.IdentityKey(request), StringComparison.Ordinal)
                || !AllocineProviderNames.IsValidId(mapping.AllocineId))
            {
                return false;
            }

            TimeSpan lifetime = mapping.Source == AllocineResolutionSource.TitleYear
                ? TimeSpan.FromDays(30)
                : TimeSpan.FromDays(180);
            TimeSpan age = _timeProvider.GetUtcNow() - mapping.ResolvedAt;
            return age >= TimeSpan.Zero && age <= lifetime;
        }

        private async Task<bool> IsUserOwnedNativeIdAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            if (!AllocineProviderNames.IsValidId(request.AllocineId))
            {
                return false;
            }

            try
            {
                AllocineNativeWriteEntry? provenance = await _store
                    .ReadNativeWriteAsync(request.ItemId, cancellationToken)
                    .ConfigureAwait(false);
                return provenance == null
                    || !string.Equals(provenance.AllocineId, request.AllocineId, StringComparison.Ordinal);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Allocine] Failed to read native-id provenance for item {ItemId}", request.ItemId);
                return true;
            }
        }

        private async Task<AllocineMappingEntry?> TryReadMappingAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _store.ReadMappingAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Allocine] Failed to read the private identity mapping cache for item {ItemId}", request.ItemId);
                return null;
            }
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
