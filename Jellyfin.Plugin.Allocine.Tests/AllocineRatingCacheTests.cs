using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Allocine.Tests;

public sealed class AllocineRatingCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"allocine-cache-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task CacheMissFetchesAndPersistsForTheNextServiceInstance()
    {
        var request = Request();
        var provider = new FakeProvider(new Dictionary<string, string> { ["presse"] = "3.7", ["public"] = "4.1" });
        var store = new AllocineRatingStore(Path.Combine(_directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
        var service = new AllocineRatingCacheService(store, provider, NullLogger<AllocineRatingCacheService>.Instance);

        Dictionary<string, string>? first = await service.GetRatingsAsync(request, CancellationToken.None);

        Assert.Equal("3.7", first?["presse"]);
        Assert.Equal(1, provider.Calls);

        var secondProvider = new FakeProvider(throwOnCall: true);
        var secondStore = new AllocineRatingStore(Path.Combine(_directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
        var secondService = new AllocineRatingCacheService(secondStore, secondProvider, NullLogger<AllocineRatingCacheService>.Instance);

        Dictionary<string, string>? second = await secondService.GetRatingsAsync(request, CancellationToken.None);

        Assert.Equal("4.1", second?["public"]);
        Assert.Equal(0, secondProvider.Calls);
    }

    [Fact]
    public async Task ConcurrentMissesShareOneRemoteFetch()
    {
        var provider = new FakeProvider(new Dictionary<string, string> { ["public"] = "4.0" }, delay: TimeSpan.FromMilliseconds(50));
        var store = new AllocineRatingStore(Path.Combine(_directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
        var service = new AllocineRatingCacheService(store, provider, NullLogger<AllocineRatingCacheService>.Instance);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.GetRatingsAsync(Request(), CancellationToken.None)));

        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task ChangedProviderIdentityDoesNotReuseTheOldRating()
    {
        var provider = new FakeProvider(new Dictionary<string, string> { ["public"] = "4.0" });
        var store = new AllocineRatingStore(Path.Combine(_directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
        var service = new AllocineRatingCacheService(store, provider, NullLogger<AllocineRatingCacheService>.Instance);

        await service.GetRatingsAsync(Request(), CancellationToken.None);
        await service.GetRatingsAsync(Request(tmdbId: "999"), CancellationToken.None);

        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task FailedLookupUsesBackoffWithoutBecomingAStoredMatch()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        var provider = new FakeProvider(result: null);
        var store = new AllocineRatingStore(Path.Combine(_directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
        var service = new AllocineRatingCacheService(store, provider, NullLogger<AllocineRatingCacheService>.Instance, time);

        Assert.Null(await service.GetRatingsAsync(Request(), CancellationToken.None));
        Assert.Null(await service.GetRatingsAsync(Request(), CancellationToken.None));
        Assert.Equal(1, provider.Calls);

        time.Advance(TimeSpan.FromHours(7));
        Assert.Null(await service.GetRatingsAsync(Request(), CancellationToken.None));

        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task AdaptiveFreshnessRefreshesNewReleasesMoreOftenThanCatalogMovies()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        var provider = new FakeProvider(new Dictionary<string, string> { ["public"] = "4.0" });
        var store = new AllocineRatingStore(Path.Combine(_directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
        var service = new AllocineRatingCacheService(store, provider, NullLogger<AllocineRatingCacheService>.Instance, time);
        AllocineRatingsRequest newRelease = Request(year: 2026, itemId: "new-release");
        AllocineRatingsRequest catalogMovie = Request(year: 2010, itemId: "catalog-movie");

        await service.GetRatingsAsync(newRelease, CancellationToken.None);
        await service.GetRatingsAsync(catalogMovie, CancellationToken.None);
        time.Advance(TimeSpan.FromDays(2));

        Assert.True(await service.NeedsRefreshAsync(newRelease, CancellationToken.None));
        Assert.False(await service.NeedsRefreshAsync(catalogMovie, CancellationToken.None));

        time.Advance(TimeSpan.FromDays(89));
        Assert.True(await service.NeedsRefreshAsync(catalogMovie, CancellationToken.None));
    }

    [Fact]
    public async Task CachedRatingsWithoutEditorialFlagsAreRefreshedOnTheNextRead()
    {
        string path = Path.Combine(_directory, "ratings.db");
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));
        var initialProvider = new FakeProvider(new Dictionary<string, string> { ["public"] = "4.4" });
        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        var initialService = new AllocineRatingCacheService(store, initialProvider, NullLogger<AllocineRatingCacheService>.Instance, time);
        AllocineRatingsRequest request = Request(year: 2011);
        await initialService.GetRatingsAsync(request, CancellationToken.None);
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE ratings SET ratings_json = '{\"public\":\"4.4\"}';";
            await command.ExecuteNonQueryAsync();
        }

        var refreshProvider = new FakeProvider(new Dictionary<string, string>
        {
            ["public"] = "4.4",
            ["classiques"] = "1",
            ["clubAime"] = "1",
        });
        var service = new AllocineRatingCacheService(store, refreshProvider, NullLogger<AllocineRatingCacheService>.Instance, time);

        Assert.False(await service.NeedsRefreshAsync(request, CancellationToken.None));
        Dictionary<string, string>? ratings = await service.GetRatingsAsync(request, CancellationToken.None);

        Assert.Equal(1, refreshProvider.Calls);
        Assert.Equal("1", ratings?["classiques"]);
        Assert.Equal("1", ratings?["clubAime"]);
    }

    [Fact]
    public async Task RatingRefreshReusesFreshExactAllocineMappingAcrossServiceInstances()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        string path = Path.Combine(_directory, "ratings.db");
        var provider = new SplitFakeProvider("325941", new Dictionary<string, string> { ["public"] = "3.8" });
        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        var firstService = new AllocineRatingCacheService(store, provider, NullLogger<AllocineRatingCacheService>.Instance, time);
        AllocineRatingsRequest request = Request(year: 2026);

        await firstService.GetRatingsAsync(request, CancellationToken.None);
        time.Advance(TimeSpan.FromDays(2));
        var secondService = new AllocineRatingCacheService(store, provider, NullLogger<AllocineRatingCacheService>.Instance, time);
        AllocineRefreshOutcome outcome = await secondService.RefreshIfNeededAsync(request, CancellationToken.None);

        Assert.Equal(AllocineRefreshResult.Updated, outcome.Result);
        Assert.Equal(1, provider.ResolveCalls);
        Assert.Equal(2, provider.RatingCalls);
    }

    [Fact]
    public async Task ExactAllocineMappingExpiresAfterOneHundredEightyDays()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var provider = new SplitFakeProvider("325941", new Dictionary<string, string> { ["public"] = "3.8" });
        var store = new AllocineRatingStore(Path.Combine(_directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
        var service = new AllocineRatingCacheService(store, provider, NullLogger<AllocineRatingCacheService>.Instance, time);
        AllocineRatingsRequest request = Request(year: 2010);

        await service.GetRatingsAsync(request, CancellationToken.None);
        time.Advance(TimeSpan.FromDays(181));
        await service.RefreshIfNeededAsync(request, CancellationToken.None);

        Assert.Equal(2, provider.ResolveCalls);
        Assert.Equal(2, provider.RatingCalls);
    }

    [Fact]
    public async Task ProviderIdentityChangeDoesNotReusePreviousAllocineMapping()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        var provider = new SplitFakeProvider("325941", new Dictionary<string, string> { ["public"] = "3.8" });
        var store = new AllocineRatingStore(Path.Combine(_directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
        var service = new AllocineRatingCacheService(store, provider, NullLogger<AllocineRatingCacheService>.Instance, time);

        await service.GetRatingsAsync(Request(tmdbId: "123"), CancellationToken.None);
        await service.GetRatingsAsync(Request(tmdbId: "999"), CancellationToken.None);

        Assert.Equal(2, provider.ResolveCalls);
    }

    [Fact]
    public async Task StaleRatingIsReturnedImmediatelyWithoutBlockingOnRemoteRefresh()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        var firstProvider = new FakeProvider(new Dictionary<string, string> { ["public"] = "3.9" });
        string path = Path.Combine(_directory, "ratings.db");
        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        var service = new AllocineRatingCacheService(store, firstProvider, NullLogger<AllocineRatingCacheService>.Instance, time);
        await service.GetRatingsAsync(Request(year: 2026), CancellationToken.None);
        time.Advance(TimeSpan.FromDays(2));
        var blockingProvider = new FakeProvider(throwOnCall: true);
        var displayService = new AllocineRatingCacheService(store, blockingProvider, NullLogger<AllocineRatingCacheService>.Instance, time);

        Dictionary<string, string>? result = await displayService.GetRatingsAsync(Request(year: 2026), CancellationToken.None);

        Assert.Equal("3.9", result?["public"]);
        Assert.Equal(0, blockingProvider.Calls);
    }

    [Fact]
    public async Task FailedRefreshPreservesTheLastKnownRatingAndStartsBackoff()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        string path = Path.Combine(_directory, "ratings.db");
        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        var initialProvider = new FakeProvider(new Dictionary<string, string> { ["public"] = "3.8" });
        var initialService = new AllocineRatingCacheService(store, initialProvider, NullLogger<AllocineRatingCacheService>.Instance, time);
        await initialService.GetRatingsAsync(Request(year: 2026), CancellationToken.None);
        time.Advance(TimeSpan.FromDays(2));
        var failingProvider = new FakeProvider(result: null);
        var refreshService = new AllocineRatingCacheService(store, failingProvider, NullLogger<AllocineRatingCacheService>.Instance, time);

        AllocineRefreshOutcome outcome = await refreshService.RefreshIfNeededAsync(Request(year: 2026), CancellationToken.None);

        Assert.Equal(AllocineRefreshResult.Failed, outcome.Result);
        Assert.Equal("3.8", outcome.Ratings?["public"]);
        Assert.False(await refreshService.NeedsRefreshAsync(Request(year: 2026), CancellationToken.None));
        Assert.Equal("3.8", (await refreshService.GetRatingsAsync(Request(year: 2026), CancellationToken.None))?["public"]);
        Assert.Equal(1, failingProvider.Calls);
    }

    [Fact]
    public async Task VersionOneDatabaseMigratesWithoutLosingRatings()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "ratings.db");
        AllocineRatingsRequest request = Request();
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
                    fetched_utc TEXT NOT NULL
                );
                INSERT INTO ratings VALUES (
                    $item_key, $identity_key, 'Movie', $item_id, 'tt1234567', '123',
                    'Example', 'Example', 2024, 1, '{"public":"4.2"}', $fetched_utc);
                PRAGMA user_version=1;
                """;
            command.Parameters.AddWithValue("$item_key", AllocineRatingStore.CacheKey(request));
            command.Parameters.AddWithValue("$identity_key", AllocineRatingStore.IdentityKey(request));
            command.Parameters.AddWithValue("$item_id", request.ItemId);
            command.Parameters.AddWithValue("$fetched_utc", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        AllocineCacheEntry? entry = await store.ReadAsync(request, CancellationToken.None);

        Assert.Equal("4.2", entry?.Ratings?["public"]);
        await using var migrated = new SqliteConnection($"Data Source={path}");
        await migrated.OpenAsync();
        await using SqliteCommand version = migrated.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(5L, await version.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task VersionTwoOrThreeMigrationDropsTransientFailuresButKeepsRatings(int priorVersion)
    {
        string path = Path.Combine(_directory, "ratings.db");
        AllocineRatingsRequest rated = Request(tmdbId: "123", itemId: "0123456789abcdef0123456789abcdef");
        AllocineRatingsRequest failed = Request(tmdbId: "456", itemId: "fedcba9876543210fedcba9876543210");
        var originalStore = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        await originalStore.WriteAsync(
            rated,
            new Dictionary<string, string> { ["public"] = "4.2" },
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await originalStore.RecordFailureAsync(failed, DateTimeOffset.UtcNow, CancellationToken.None);
        originalStore.Dispose();
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using SqliteCommand downgrade = connection.CreateCommand();
            downgrade.CommandText = priorVersion == 2
                ? "DROP TABLE mappings; PRAGMA user_version=2;"
                : "PRAGMA user_version=3;";
            await downgrade.ExecuteNonQueryAsync();
        }

        var migratedStore = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        AllocineCacheEntry? preserved = await migratedStore.ReadAsync(rated, CancellationToken.None);

        Assert.Equal("4.2", preserved?.Ratings?["public"]);
        await using var migrated = new SqliteConnection($"Data Source={path}");
        await migrated.OpenAsync();
        await using SqliteCommand failures = migrated.CreateCommand();
        failures.CommandText = "SELECT COUNT(*) FROM ratings WHERE found=0 AND ratings_json IS NULL;";
        Assert.Equal(0L, await failures.ExecuteScalarAsync());
        await using SqliteCommand version = migrated.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(5L, await version.ExecuteScalarAsync());
    }

    [Fact]
    public async Task MalformedCacheRowIsDeletedSoFailureBackoffCanRecover()
    {
        string path = Path.Combine(_directory, "ratings.db");
        var initialProvider = new FakeProvider(new Dictionary<string, string> { ["public"] = "4.0" });
        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        var initialService = new AllocineRatingCacheService(store, initialProvider, NullLogger<AllocineRatingCacheService>.Instance);
        await initialService.GetRatingsAsync(Request(), CancellationToken.None);
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE ratings SET ratings_json = 'not-json';";
            await command.ExecuteNonQueryAsync();
        }

        var failingProvider = new FakeProvider(result: null);
        var service = new AllocineRatingCacheService(store, failingProvider, NullLogger<AllocineRatingCacheService>.Instance);
        Assert.Null(await service.GetRatingsAsync(Request(), CancellationToken.None));
        Assert.Null(await service.GetRatingsAsync(Request(), CancellationToken.None));

        Assert.Equal(1, failingProvider.Calls);
    }

    [Fact]
    public async Task CorruptCustomDatabaseIsQuarantinedAndRecreated()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "ratings.db");
        await File.WriteAllTextAsync(path, "not a sqlite database");
        var provider = new FakeProvider(new Dictionary<string, string> { ["public"] = "4.2" });
        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        var service = new AllocineRatingCacheService(store, provider, NullLogger<AllocineRatingCacheService>.Instance);

        Dictionary<string, string>? result = await service.GetRatingsAsync(Request(), CancellationToken.None);

        Assert.Equal("4.2", result?["public"]);
        Assert.NotEmpty(Directory.GetFiles(_directory, "ratings.db.corrupt-*"));
    }

    [Fact]
    public async Task CorruptionAfterInitializationIsQuarantinedAndRetriedOnce()
    {
        string path = Path.Combine(_directory, "ratings.db");
        var provider = new FakeProvider(new Dictionary<string, string> { ["public"] = "4.2" });
        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        var service = new AllocineRatingCacheService(store, provider, NullLogger<AllocineRatingCacheService>.Instance);
        await service.GetRatingsAsync(Request(), CancellationToken.None);
        SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(path, "not a sqlite database");

        Dictionary<string, string>? result = await service.GetRatingsAsync(Request(), CancellationToken.None);

        Assert.Equal("4.2", result?["public"]);
        Assert.Equal(2, provider.Calls);
        Assert.NotEmpty(Directory.GetFiles(_directory, "ratings.db.corrupt-*"));
    }

    [Fact]
    public async Task MappingWriteAfterCorruptionIsQuarantinedAndRetriedOnce()
    {
        string path = Path.Combine(_directory, "ratings.db");
        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        AllocineRatingsRequest request = Request();
        await store.ReadAsync(request, CancellationToken.None);
        SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(path, "not a sqlite database");

        await store.WriteMappingAsync(request, "24680", DateTimeOffset.UtcNow, CancellationToken.None);
        AllocineMappingEntry? mapping = await store.ReadMappingAsync(request, CancellationToken.None);

        Assert.Equal("24680", mapping?.AllocineId);
        Assert.NotEmpty(Directory.GetFiles(_directory, "ratings.db.corrupt-*"));
    }

    [Fact]
    public async Task MappingReadAfterCorruptionIsQuarantinedAndReturnsAMiss()
    {
        string path = Path.Combine(_directory, "ratings.db");
        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        AllocineRatingsRequest request = Request();
        await store.WriteMappingAsync(request, "24680", DateTimeOffset.UtcNow, CancellationToken.None);
        SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(path, "not a sqlite database");

        AllocineMappingEntry? mapping = await store.ReadMappingAsync(request, CancellationToken.None);

        Assert.Null(mapping);
        Assert.NotEmpty(Directory.GetFiles(_directory, "ratings.db.corrupt-*"));
    }

    [Fact]
    public async Task NewerSchemaIsRejectedInsteadOfRelabeled()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "ratings.db");
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=6;";
            await command.ExecuteNonQueryAsync();
        }

        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ReadAsync(Request(), CancellationToken.None));
        Assert.Contains("newer than supported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredExactMappingIsReResolvedInsteadOfTrustingTheNativeId()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        string path = Path.Combine(_directory, "ratings.db");
        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        AllocineRatingsRequest request = Request(year: 2011) with { AllocineId = "111" };
        await store.WriteMappingAsync(
            request,
            "111",
            time.GetUtcNow(),
            AllocineResolutionSource.ExactIdentifiers,
            CancellationToken.None);
        await store.RecordNativeWriteAsync(
            request.ItemId,
            "111",
            AllocineRatingStore.IdentityKey(request),
            time.GetUtcNow(),
            CancellationToken.None);
        await store.WriteAsync(
            request,
            new Dictionary<string, string> { ["public"] = "1.0" },
            time.GetUtcNow(),
            CancellationToken.None);
        time.Advance(TimeSpan.FromDays(181));
        var provider = new RedirectingMappingProvider("222", new Dictionary<string, string> { ["public"] = "4.8" });
        var service = new AllocineRatingCacheService(store, provider, NullLogger<AllocineRatingCacheService>.Instance, time);

        AllocineRefreshOutcome outcome = await service.RefreshIfNeededAsync(request, CancellationToken.None);

        Assert.Equal(AllocineRefreshResult.Updated, outcome.Result);
        Assert.Equal("4.8", outcome.Ratings?["public"]);
        Assert.Equal("222", provider.RatedAllocineId);
        Assert.Equal(1, provider.ExactCalls);
        AllocineMappingEntry? mapping = await store.ReadMappingAsync(request, CancellationToken.None);
        Assert.Equal("222", mapping?.AllocineId);
        Assert.Equal(AllocineResolutionSource.ExactIdentifiers, mapping?.Source);
    }

    [Fact]
    public async Task TransientExactResolveDoesNotOverwriteExactMappingWithTitleYear()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        string path = Path.Combine(_directory, "ratings.db");
        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        AllocineRatingsRequest request = Request(year: 2011) with { AllocineId = "111" };
        await store.WriteMappingAsync(
            request,
            "111",
            time.GetUtcNow(),
            AllocineResolutionSource.ExactIdentifiers,
            CancellationToken.None);
        await store.RecordNativeWriteAsync(
            request.ItemId,
            "111",
            AllocineRatingStore.IdentityKey(request),
            time.GetUtcNow(),
            CancellationToken.None);
        await store.WriteAsync(
            request,
            new Dictionary<string, string> { ["public"] = "1.0" },
            time.GetUtcNow(),
            CancellationToken.None);
        time.Advance(TimeSpan.FromDays(181));
        var provider = new TransientThenTitleYearProvider();
        var service = new AllocineRatingCacheService(store, provider, NullLogger<AllocineRatingCacheService>.Instance, time);

        AllocineRefreshOutcome outcome = await service.RefreshIfNeededAsync(request, CancellationToken.None);

        Assert.Equal(AllocineRefreshResult.Failed, outcome.Result);
        AllocineMappingEntry? mapping = await store.ReadMappingAsync(request, CancellationToken.None);
        Assert.Equal("111", mapping?.AllocineId);
        Assert.Equal(AllocineResolutionSource.ExactIdentifiers, mapping?.Source);
        Assert.Equal(1, provider.ExactCalls);
        Assert.Equal(0, provider.TitleYearCalls);
    }

    [Fact]
    public async Task AuthoritativeExactMissDoesNotOverwriteExactMappingWithTitleYear()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        string path = Path.Combine(_directory, "ratings.db");
        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        AllocineRatingsRequest request = Request(year: 2011) with { AllocineId = "111" };
        await store.WriteMappingAsync(
            request,
            "111",
            time.GetUtcNow(),
            AllocineResolutionSource.ExactIdentifiers,
            CancellationToken.None);
        await store.RecordNativeWriteAsync(
            request.ItemId,
            "111",
            AllocineRatingStore.IdentityKey(request),
            time.GetUtcNow(),
            CancellationToken.None);
        await store.WriteAsync(
            request,
            new Dictionary<string, string> { ["public"] = "1.0" },
            time.GetUtcNow(),
            CancellationToken.None);
        time.Advance(TimeSpan.FromDays(181));
        var provider = new ExactMissThenTitleYearProvider();
        var service = new AllocineRatingCacheService(store, provider, NullLogger<AllocineRatingCacheService>.Instance, time);

        AllocineRefreshOutcome outcome = await service.RefreshIfNeededAsync(request, CancellationToken.None);

        AllocineMappingEntry? mapping = await store.ReadMappingAsync(request, CancellationToken.None);
        Assert.Equal("111", mapping?.AllocineId);
        Assert.Equal(AllocineResolutionSource.ExactIdentifiers, mapping?.Source);
        Assert.Equal(1, provider.ExactCalls);
        Assert.Equal(0, provider.TitleYearCalls);
        Assert.Equal("111", provider.RatedAllocineId);
        Assert.Equal(AllocineRefreshResult.Updated, outcome.Result);
        Assert.Equal("4.8", outcome.Ratings?["public"]);
    }

    [Fact]
    public async Task WikidataHttpFailureDoesNotExactMissAnUnknownMapping()
    {
        using var httpClient = new HttpClient(new AlwaysFailHandler());
        using var mapping = new AllocineService(NullLogger<AllocineService>.Instance, httpClient);
        string path = Path.Combine(_directory, "ratings.db");
        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        AllocineRatingsRequest request = Request();
        await store.WriteMappingAsync(
            request,
            "999",
            DateTimeOffset.UtcNow,
            AllocineResolutionSource.Unknown,
            CancellationToken.None);
        var service = new AllocineRatingCacheService(store, mapping, NullLogger<AllocineRatingCacheService>.Instance);

        string? exactId = await service.GetExactAllocineIdAsync(request, CancellationToken.None);

        Assert.Null(exactId);
        AllocineMappingEntry? persisted = await store.ReadMappingAsync(request, CancellationToken.None);
        Assert.Equal("999", persisted?.AllocineId);
        Assert.Equal(AllocineResolutionSource.Unknown, persisted?.Source);
    }

    [Fact]
    public async Task SecondResolveExactIdentifiersIsPersistedAsExactNotDropped()
    {
        var provider = new ExactMissThenExactIdentifiersProvider();
        var store = new AllocineRatingStore(Path.Combine(_directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
        var service = new AllocineRatingCacheService(store, provider, NullLogger<AllocineRatingCacheService>.Instance);
        AllocineRatingsRequest request = Request();

        string? exactId = await service.GetExactAllocineIdAsync(request, CancellationToken.None);

        Assert.Equal("256880", exactId);
        AllocineMappingEntry? mapping = await store.ReadMappingAsync(request, CancellationToken.None);
        Assert.Equal("256880", mapping?.AllocineId);
        Assert.Equal(AllocineResolutionSource.ExactIdentifiers, mapping?.Source);
        Assert.Equal(1, provider.ExactCalls);
        Assert.Equal(1, provider.FallbackCalls);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static AllocineRatingsRequest Request(
        string tmdbId = "123",
        int year = 2024,
        string itemId = "0123456789abcdef0123456789abcdef") => new(
        itemId,
        "Movie",
        "Example",
        "Example",
        year,
        "tt1234567",
        tmdbId);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class FakeProvider : IAllocineRatingProvider
    {
        private readonly Dictionary<string, string>? _result;
        private readonly bool _throwOnCall;
        private readonly TimeSpan _delay;
        private int _calls;

        public FakeProvider(Dictionary<string, string>? result = null, bool throwOnCall = false, TimeSpan delay = default)
        {
            _result = result == null ? null : AllocineEditorialFlags.WithDefaults(result);
            _throwOnCall = throwOnCall;
            _delay = delay;
        }

        public int Calls => Volatile.Read(ref _calls);

        public async Task<Dictionary<string, string>?> GetRatingsAsync(AllocineRatingsRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (_throwOnCall)
            {
                throw new InvalidOperationException("The provider must not be called on a cache hit.");
            }

            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken);
            }

            return _result == null ? null : new Dictionary<string, string>(_result);
        }
    }

    private sealed class SplitFakeProvider(
        string allocineId,
        Dictionary<string, string> ratings) : IAllocineMappingProvider
    {
        public int ResolveCalls { get; private set; }

        public int RatingCalls { get; private set; }

        public Task<AllocineResolvedIdentity> ResolveIdentityAsync(
            AllocineRatingsRequest request,
            bool allowTitleYearFallback,
            CancellationToken cancellationToken)
        {
            ResolveCalls++;
            return Task.FromResult(new AllocineResolvedIdentity(
                allocineId,
                AllocineResolutionSource.ExactIdentifiers,
                false));
        }

        public async Task<string?> ResolveAllocineIdAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            return (await ResolveIdentityAsync(request, true, cancellationToken)).AllocineId;
        }

        public Task<Dictionary<string, string>?> GetRatingsByAllocineIdAsync(
            string resolvedAllocineId,
            string mediaType,
            CancellationToken cancellationToken)
        {
            RatingCalls++;
            Assert.Equal(allocineId, resolvedAllocineId);
            return Task.FromResult<Dictionary<string, string>?>(AllocineEditorialFlags.WithDefaults(ratings));
        }

        public async Task<Dictionary<string, string>?> GetRatingsAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            string? resolved = await ResolveAllocineIdAsync(request, cancellationToken);
            return resolved == null
                ? null
                : await GetRatingsByAllocineIdAsync(resolved, request.MediaType, cancellationToken);
        }
    }

    private sealed class RedirectingMappingProvider(string allocineId, Dictionary<string, string> ratings)
        : IAllocineMappingProvider
    {
        public int ExactCalls { get; private set; }

        public string? RatedAllocineId { get; private set; }

        public Task<AllocineResolvedIdentity> ResolveIdentityAsync(
            AllocineRatingsRequest request,
            bool allowTitleYearFallback,
            CancellationToken cancellationToken)
        {
            if (!allowTitleYearFallback)
            {
                ExactCalls++;
            }

            return Task.FromResult(new AllocineResolvedIdentity(
                allocineId,
                AllocineResolutionSource.ExactIdentifiers,
                false));
        }

        public async Task<string?> ResolveAllocineIdAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            return (await ResolveIdentityAsync(request, false, cancellationToken)).AllocineId;
        }

        public Task<Dictionary<string, string>?> GetRatingsByAllocineIdAsync(
            string resolvedAllocineId,
            string mediaType,
            CancellationToken cancellationToken)
        {
            RatedAllocineId = resolvedAllocineId;
            return Task.FromResult<Dictionary<string, string>?>(AllocineEditorialFlags.WithDefaults(ratings));
        }

        public async Task<Dictionary<string, string>?> GetRatingsAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            string? resolved = await ResolveAllocineIdAsync(request, cancellationToken);
            return resolved == null
                ? null
                : await GetRatingsByAllocineIdAsync(resolved, request.MediaType, cancellationToken);
        }
    }

    private sealed class TransientThenTitleYearProvider : IAllocineMappingProvider
    {
        public int ExactCalls { get; private set; }

        public int TitleYearCalls { get; private set; }

        public Task<AllocineResolvedIdentity> ResolveIdentityAsync(
            AllocineRatingsRequest request,
            bool allowTitleYearFallback,
            CancellationToken cancellationToken)
        {
            if (!allowTitleYearFallback)
            {
                ExactCalls++;
                return Task.FromResult(new AllocineResolvedIdentity(
                    null,
                    AllocineResolutionSource.None,
                    false,
                    true));
            }

            TitleYearCalls++;
            return Task.FromResult(new AllocineResolvedIdentity(
                "999",
                AllocineResolutionSource.TitleYear,
                false));
        }

        public async Task<string?> ResolveAllocineIdAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            return (await ResolveIdentityAsync(request, true, cancellationToken)).AllocineId;
        }

        public Task<Dictionary<string, string>?> GetRatingsByAllocineIdAsync(
            string resolvedAllocineId,
            string mediaType,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<Dictionary<string, string>?>(AllocineEditorialFlags.WithDefaults(new Dictionary<string, string> { ["public"] = "4.8" }));
        }

        public async Task<Dictionary<string, string>?> GetRatingsAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            string? resolved = await ResolveAllocineIdAsync(request, cancellationToken);
            return resolved == null
                ? null
                : await GetRatingsByAllocineIdAsync(resolved, request.MediaType, cancellationToken);
        }
    }

    private sealed class ExactMissThenTitleYearProvider : IAllocineMappingProvider
    {
        public int ExactCalls { get; private set; }

        public int TitleYearCalls { get; private set; }

        public string? RatedAllocineId { get; private set; }

        public Task<AllocineResolvedIdentity> ResolveIdentityAsync(
            AllocineRatingsRequest request,
            bool allowTitleYearFallback,
            CancellationToken cancellationToken)
        {
            if (!allowTitleYearFallback)
            {
                ExactCalls++;
                return Task.FromResult(new AllocineResolvedIdentity(
                    null,
                    AllocineResolutionSource.None,
                    false));
            }

            TitleYearCalls++;
            return Task.FromResult(new AllocineResolvedIdentity(
                "999",
                AllocineResolutionSource.TitleYear,
                false));
        }

        public async Task<string?> ResolveAllocineIdAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            return (await ResolveIdentityAsync(request, true, cancellationToken)).AllocineId;
        }

        public Task<Dictionary<string, string>?> GetRatingsByAllocineIdAsync(
            string resolvedAllocineId,
            string mediaType,
            CancellationToken cancellationToken)
        {
            RatedAllocineId = resolvedAllocineId;
            return Task.FromResult<Dictionary<string, string>?>(AllocineEditorialFlags.WithDefaults(new Dictionary<string, string> { ["public"] = "4.8" }));
        }

        public async Task<Dictionary<string, string>?> GetRatingsAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            string? resolved = await ResolveAllocineIdAsync(request, cancellationToken);
            return resolved == null
                ? null
                : await GetRatingsByAllocineIdAsync(resolved, request.MediaType, cancellationToken);
        }
    }

    private sealed class ExactMissThenExactIdentifiersProvider : IAllocineMappingProvider
    {
        public int ExactCalls { get; private set; }

        public int FallbackCalls { get; private set; }

        public Task<AllocineResolvedIdentity> ResolveIdentityAsync(
            AllocineRatingsRequest request,
            bool allowTitleYearFallback,
            CancellationToken cancellationToken)
        {
            if (!allowTitleYearFallback)
            {
                ExactCalls++;
                return Task.FromResult(new AllocineResolvedIdentity(
                    null,
                    AllocineResolutionSource.None,
                    false));
            }

            FallbackCalls++;
            return Task.FromResult(new AllocineResolvedIdentity(
                "256880",
                AllocineResolutionSource.ExactIdentifiers,
                false));
        }

        public async Task<string?> ResolveAllocineIdAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            return (await ResolveIdentityAsync(request, true, cancellationToken)).AllocineId;
        }

        public Task<Dictionary<string, string>?> GetRatingsByAllocineIdAsync(
            string resolvedAllocineId,
            string mediaType,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<Dictionary<string, string>?>(null);
        }

        public Task<Dictionary<string, string>?> GetRatingsAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<Dictionary<string, string>?>(null);
        }
    }

    private sealed class AlwaysFailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        }
    }
}
