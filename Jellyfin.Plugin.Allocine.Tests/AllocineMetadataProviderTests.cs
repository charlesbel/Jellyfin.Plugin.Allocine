using System.Globalization;
using System.Net.Http;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.Allocine.Tests;

public sealed class AllocineMetadataProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"allocine-metadata-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ProviderIsAPostRefreshCustomEnricherAndNotARemoteFetcher()
    {
        Type type = typeof(AllocineMetadataProvider);

        Assert.True(typeof(ICustomMetadataProvider<Movie>).IsAssignableFrom(type));
        Assert.True(typeof(ICustomMetadataProvider<Series>).IsAssignableFrom(type));
        Assert.False(typeof(IPreRefreshProvider).IsAssignableFrom(type));
        Assert.False(typeof(IForcedProvider).IsAssignableFrom(type));
        Assert.False(typeof(IRemoteMetadataProvider<Movie, MovieInfo>).IsAssignableFrom(type));
        Assert.False(typeof(IRemoteMetadataProvider<Series, SeriesInfo>).IsAssignableFrom(type));
    }

    [Fact]
    public async Task MissingNativeIdIsWrittenAfterExactImdbOrTmdbResolution()
    {
        var movie = Movie("tt1234567", "123");
        RecordingMappingProvider mapping = Exact("190918");
        AllocineMetadataProvider provider = CreateProvider(mapping);

        ItemUpdateType update = await provider.FetchAsync(movie, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.MetadataImport, update);
        Assert.Equal("190918", movie.GetProviderId(AllocineProviderNames.Key));
        Assert.Null(movie.CommunityRating);
        Assert.Null(movie.CriticRating);
        Assert.Null(movie.CustomRating);
        Assert.Null(movie.OfficialRating);
        Assert.Equal(1, mapping.ExactCalls);
        Assert.Equal(0, mapping.FallbackCalls);
    }

    [Fact]
    public async Task UnchangedNativeIdDoesNotUpdateTheItemOrRepeatExactResolution()
    {
        var movie = Movie("tt1234567", "123");
        RecordingMappingProvider mapping = Exact("190918");
        AllocineMetadataProvider provider = CreateProvider(mapping);

        await provider.FetchAsync(movie, Options(), CancellationToken.None);
        ItemUpdateType second = await provider.FetchAsync(movie, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.None, second);
        Assert.Equal("190918", movie.GetProviderId(AllocineProviderNames.Key));
        Assert.Equal(1, mapping.ExactCalls);
    }

    [Fact]
    public async Task TitleYearFallbackDoesNotPersistANativeId()
    {
        var movie = new Movie
        {
            Id = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
            Name = "Only A Title",
            OriginalTitle = "Only A Title",
            ProductionYear = 2024,
        };
        var mapping = new RecordingMappingProvider { TitleYearId = "999" };
        AllocineMetadataProvider provider = CreateProvider(mapping);

        ItemUpdateType update = await provider.FetchAsync(movie, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.None, update);
        Assert.Null(movie.GetProviderId(AllocineProviderNames.Key));
        Assert.Equal(0, mapping.ExactCalls);
    }

    [Fact]
    public async Task TitleYearFallbackAfterExactMissWithStableIdsPersistsNativeId()
    {
        var movie = Movie("tt36073210", "1440098");
        movie.Name = "Attirés malgré nous";
        movie.OriginalTitle = "Enfrentados: Marfil";
        movie.ProductionYear = 2026;
        var mapping = new RecordingMappingProvider { TitleYearId = "1000020435" };
        AllocineMetadataProvider provider = CreateProvider(mapping);

        ItemUpdateType update = await provider.FetchAsync(movie, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.MetadataImport, update);
        Assert.Equal("1000020435", movie.GetProviderId(AllocineProviderNames.Key));
        Assert.Equal(1, mapping.ExactCalls);
        Assert.Equal(1, mapping.FallbackCalls);
    }

    [Fact]
    public async Task ConflictingUpstreamIdsLeaveExistingStateUntouched()
    {
        var movie = Movie("tt1234567", "123");
        movie.TrySetProviderId(AllocineProviderNames.Key, "190918");
        movie.CommunityRating = 8.2f;
        movie.CriticRating = 91f;
        movie.CustomRating = "12";
        movie.OfficialRating = "PG-13";
        var mapping = new RecordingMappingProvider { Conflict = true };
        AllocineMetadataProvider provider = CreateProvider(mapping);

        ItemUpdateType update = await provider.FetchAsync(movie, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.None, update);
        Assert.Equal("190918", movie.GetProviderId(AllocineProviderNames.Key));
        Assert.Equal(8.2f, movie.CommunityRating);
        Assert.Equal(91f, movie.CriticRating);
        Assert.Equal("12", movie.CustomRating);
        Assert.Equal("PG-13", movie.OfficialRating);
    }

    [Fact]
    public async Task NetworkFailureDoesNotDeleteAValidNativeId()
    {
        var movie = Movie("tt1234567", "123");
        movie.TrySetProviderId(AllocineProviderNames.Key, "190918");
        var mapping = new RecordingMappingProvider { ThrowOnExact = true };
        AllocineMetadataProvider provider = CreateProvider(mapping);

        ItemUpdateType update = await provider.FetchAsync(movie, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.None, update);
        Assert.Equal("190918", movie.GetProviderId(AllocineProviderNames.Key));
    }

    [Fact]
    public async Task PluginOwnedNativeIdIsReplacedWhenANewExactIdentityIsProven()
    {
        var movie = Movie("tt1234567", "123");
        RecordingMappingProvider first = Exact("111");
        AllocineMetadataProvider provider = CreateProvider(first);
        await provider.FetchAsync(movie, Options(), CancellationToken.None);
        Assert.Equal("111", movie.GetProviderId(AllocineProviderNames.Key));

        movie.SetProviderId(MetadataProvider.Tmdb, "999");
        RecordingMappingProvider second = Exact("222");
        AllocineMetadataProvider replacement = CreateProvider(second, reuseStoreFrom: provider);

        ItemUpdateType update = await replacement.FetchAsync(movie, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.MetadataImport, update);
        Assert.Equal("222", movie.GetProviderId(AllocineProviderNames.Key));
    }

    [Fact]
    public async Task UserOwnedNativeIdIsNotReplacedByADifferentExactMatch()
    {
        var movie = Movie("tt1234567", "123");
        movie.TrySetProviderId(AllocineProviderNames.Key, "555");
        RecordingMappingProvider mapping = Exact("190918");
        AllocineMetadataProvider provider = CreateProvider(mapping);

        ItemUpdateType update = await provider.FetchAsync(movie, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.None, update);
        Assert.Equal("555", movie.GetProviderId(AllocineProviderNames.Key));
    }

    [Fact]
    public async Task MatchingUserOwnedNativeIdIsNotClaimedThenReplaced()
    {
        var movie = Movie("tt1234567", "123");
        movie.TrySetProviderId(AllocineProviderNames.Key, "190918");
        RecordingMappingProvider first = Exact("190918");
        AllocineMetadataProvider provider = CreateProvider(first);
        await provider.FetchAsync(movie, Options(), CancellationToken.None);
        Assert.Equal("190918", movie.GetProviderId(AllocineProviderNames.Key));

        movie.SetProviderId(MetadataProvider.Tmdb, "999");
        RecordingMappingProvider second = Exact("222");
        AllocineMetadataProvider replacement = CreateProvider(second, reuseStoreFrom: provider);

        ItemUpdateType update = await replacement.FetchAsync(movie, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.None, update);
        Assert.Equal("190918", movie.GetProviderId(AllocineProviderNames.Key));
    }

    [Fact]
    public async Task MigratedV4MappingWithProviderIdsDoesNotBecomeANativeIdWithoutExactProof()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "ratings.db");
        var movie = Movie("tt1234567", "123");
        Assert.True(AllocineRefreshTask.TryCreateRequest(movie, out AllocineRatingsRequest request));
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE ratings (
                    item_key TEXT PRIMARY KEY NOT NULL,
                    identity_key TEXT NOT NULL,
                    media_type TEXT NOT NULL,
                    jellyfin_item_id TEXT NULL,
                    imdb_id TEXT NULL,
                    tmdb_id TEXT NULL,
                    title TEXT NOT NULL,
                    original_title TEXT NULL,
                    production_year INTEGER NOT NULL,
                    found INTEGER NOT NULL,
                    ratings_json TEXT NULL,
                    fetched_utc TEXT NOT NULL,
                    last_attempt_utc TEXT NULL,
                    consecutive_failures INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE mappings (
                    identity_key TEXT PRIMARY KEY NOT NULL,
                    media_type TEXT NOT NULL,
                    imdb_id TEXT NULL,
                    tmdb_id TEXT NULL,
                    allocine_id TEXT NOT NULL,
                    resolved_utc TEXT NOT NULL
                );
                INSERT INTO mappings VALUES (
                    $identity_key, 'Movie', 'tt1234567', '123', '999', $resolved_utc);
                PRAGMA user_version=4;
                """;
            command.Parameters.AddWithValue("$identity_key", AllocineRatingStore.IdentityKey(request));
            command.Parameters.AddWithValue("$resolved_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync();
        }

        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        var mapping = new RecordingMappingProvider();
        AllocineMetadataProvider provider = CreateProvider(mapping, store: store);

        ItemUpdateType update = await provider.FetchAsync(movie, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.None, update);
        Assert.Null(movie.GetProviderId(AllocineProviderNames.Key));
        Assert.Equal(1, mapping.ExactCalls);
        Assert.Equal(1, mapping.FallbackCalls);
    }

    [Fact]
    public async Task UnparseableMappingSourceIsNotTreatedAsExact()
    {
        var movie = Movie("tt1234567", "123");
        Assert.True(AllocineRefreshTask.TryCreateRequest(movie, out AllocineRatingsRequest request));
        string path = Path.Combine(_directory, "ratings.db");
        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        await store.WriteMappingAsync(
            request,
            "999",
            DateTimeOffset.UtcNow,
            AllocineResolutionSource.ExactIdentifiers,
            CancellationToken.None);
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE mappings SET source = 'not-a-source';";
            await command.ExecuteNonQueryAsync();
        }

        var mapping = new RecordingMappingProvider();
        AllocineMetadataProvider provider = CreateProvider(mapping, store: store);

        ItemUpdateType update = await provider.FetchAsync(movie, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.None, update);
        Assert.Null(movie.GetProviderId(AllocineProviderNames.Key));
        Assert.Equal(1, mapping.ExactCalls);
        Assert.Equal(1, mapping.FallbackCalls);
    }

    [Fact]
    public async Task DisabledOptionSkipsNativeIdWritesButStillRefreshesThePrivateCache()
    {
        var movie = Movie("tt1234567", "123");
        RecordingMappingProvider mapping = Exact("190918");
        var configuration = new PluginConfiguration { WriteNativeAllocineIds = false };
        AllocineMetadataProvider provider = CreateProvider(mapping, configuration: configuration);

        ItemUpdateType update = await provider.FetchAsync(movie, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.None, update);
        Assert.Null(movie.GetProviderId(AllocineProviderNames.Key));
        Assert.Equal("4.1", (await Ratings(provider, movie))["public"]);
    }

    [Fact]
    public async Task LockedItemsAreLeftIntact()
    {
        var movie = Movie("tt1234567", "123");
        movie.IsLocked = true;
        RecordingMappingProvider mapping = Exact("190918");
        AllocineMetadataProvider provider = CreateProvider(mapping);

        ItemUpdateType update = await provider.FetchAsync(movie, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.None, update);
        Assert.Null(movie.GetProviderId(AllocineProviderNames.Key));
        Assert.Equal(0, mapping.ExactCalls);
        Assert.Equal(0, mapping.RatingCalls);
    }

    [Fact]
    public async Task SeriesNativeIdAndPrivateCacheAreUpdatedTogether()
    {
        var series = new Series
        {
            Id = Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210"),
            Name = "The Office",
            OriginalTitle = "The Office",
            ProductionYear = 2005,
        };
        series.SetProviderId(MetadataProvider.Imdb, "tt0386676");
        series.SetProviderId(MetadataProvider.Tmdb, "2316");
        RecordingMappingProvider mapping = Exact("331");
        AllocineMetadataProvider provider = CreateProvider(mapping);

        ItemUpdateType update = await provider.FetchAsync(series, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.MetadataImport, update);
        Assert.Equal("331", series.GetProviderId(AllocineProviderNames.Key));
        Assert.Equal("4.1", (await Ratings(provider, series))["public"]);
    }

    [Fact]
    public async Task RequestCancellationIsPropagated()
    {
        var movie = Movie("tt1234567", "123");
        var mapping = new RecordingMappingProvider { StallExact = true };
        AllocineMetadataProvider provider = CreateProvider(mapping);
        using var cancellation = new CancellationTokenSource();
        Task<ItemUpdateType> task = provider.FetchAsync(movie, Options(), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Null(movie.GetProviderId(AllocineProviderNames.Key));
    }

    [Fact]
    public async Task ChangedAllocineIdDoesNotReuseThePreviousPrivateRating()
    {
        var movie = Movie("tt1234567", "123");
        var store = new AllocineRatingStore(Path.Combine(_directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
        AllocineRatingsRequest original = AllocineRefreshTask.TryCreateRequest(movie, out AllocineRatingsRequest request)
            ? request with { AllocineId = "111" }
            : throw new InvalidOperationException();
        await store.WriteAsync(
            original,
            new Dictionary<string, string> { ["public"] = "1.0" },
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        movie.TrySetProviderId(AllocineProviderNames.Key, "222");
        var mapping = new RecordingMappingProvider
        {
            ExactId = "222",
            Ratings = new Dictionary<string, string> { ["public"] = "4.8" },
        };
        AllocineMetadataProvider provider = CreateProvider(mapping, store: store);
        await provider.FetchAsync(movie, Options(), CancellationToken.None);

        Assert.Equal("4.8", (await Ratings(provider, movie))["public"]);
        Assert.Equal(1, mapping.RatingCalls);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private AllocineMetadataProvider CreateProvider(
        RecordingMappingProvider mapping,
        PluginConfiguration? configuration = null,
        AllocineRatingStore? store = null,
        AllocineMetadataProvider? reuseStoreFrom = null)
    {
        store ??= reuseStoreFrom?.Store
            ?? new AllocineRatingStore(Path.Combine(_directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
        var cache = new AllocineRatingCacheService(store, mapping, NullLogger<AllocineRatingCacheService>.Instance);
        return new AllocineMetadataProvider(
            cache,
            mapping,
            store,
            NullLogger<AllocineMetadataProvider>.Instance,
            configuration ?? new PluginConfiguration());
    }

    private static async Task<Dictionary<string, string>> Ratings(AllocineMetadataProvider provider, BaseItem item)
    {
        Assert.True(AllocineRefreshTask.TryCreateRequest(item, out AllocineRatingsRequest request));
        Dictionary<string, string>? ratings = await provider.Cache.GetRatingsAsync(request, CancellationToken.None);
        Assert.NotNull(ratings);
        return ratings!;
    }

    private static Movie Movie(string imdbId, string tmdbId)
    {
        var movie = new Movie
        {
            Id = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
            Name = "Intouchables",
            OriginalTitle = "Intouchables",
            ProductionYear = 2011,
        };
        movie.SetProviderId(MetadataProvider.Imdb, imdbId);
        movie.SetProviderId(MetadataProvider.Tmdb, tmdbId);
        return movie;
    }

    private static MetadataRefreshOptions Options()
    {
        return new MetadataRefreshOptions(Mock.Of<IDirectoryService>());
    }

    private static RecordingMappingProvider Exact(string allocineId)
    {
        return new RecordingMappingProvider { ExactId = allocineId };
    }

    private sealed class RecordingMappingProvider : IAllocineMappingProvider
    {
        public string? ExactId { get; set; }

        public string? TitleYearId { get; set; }

        public bool Conflict { get; set; }

        public bool ThrowOnExact { get; set; }

        public bool StallExact { get; set; }

        public Dictionary<string, string> Ratings { get; set; } = new() { ["public"] = "4.1" };

        public int ExactCalls { get; private set; }

        public int FallbackCalls { get; private set; }

        public int RatingCalls { get; private set; }

        public async Task<AllocineResolvedIdentity> ResolveIdentityAsync(
            AllocineRatingsRequest request,
            bool allowTitleYearFallback,
            CancellationToken cancellationToken)
        {
            if (!allowTitleYearFallback)
            {
                ExactCalls++;
                if (StallExact)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                if (ThrowOnExact)
                {
                    throw new HttpRequestException("Wikidata unavailable");
                }

                if (Conflict)
                {
                    return new AllocineResolvedIdentity(null, AllocineResolutionSource.None, true);
                }

                return ExactId == null
                    ? new AllocineResolvedIdentity(null, AllocineResolutionSource.None, false)
                    : new AllocineResolvedIdentity(ExactId, AllocineResolutionSource.ExactIdentifiers, false);
            }

            FallbackCalls++;
            return TitleYearId == null
                ? new AllocineResolvedIdentity(null, AllocineResolutionSource.None, false)
                : new AllocineResolvedIdentity(TitleYearId, AllocineResolutionSource.TitleYear, false);
        }

        public async Task<string?> ResolveAllocineIdAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            AllocineResolvedIdentity exact = await ResolveIdentityAsync(request, false, cancellationToken);
            if (exact.AllocineId != null || exact.IsConflict)
            {
                return exact.AllocineId;
            }

            return (await ResolveIdentityAsync(request, true, cancellationToken)).AllocineId;
        }

        public Task<Dictionary<string, string>?> GetRatingsByAllocineIdAsync(
            string allocineId,
            string mediaType,
            CancellationToken cancellationToken)
        {
            RatingCalls++;
            return Task.FromResult<Dictionary<string, string>?>(new Dictionary<string, string>(Ratings));
        }

        public async Task<Dictionary<string, string>?> GetRatingsAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            string? allocineId = await ResolveAllocineIdAsync(request, cancellationToken);
            return allocineId == null
                ? null
                : await GetRatingsByAllocineIdAsync(allocineId, request.MediaType, cancellationToken);
        }
    }
}
