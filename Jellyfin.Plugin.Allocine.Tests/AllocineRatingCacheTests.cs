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
    public async Task FailedLookupRetriesWhenAnAllocineIdBecomesKnown()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 25, 10, 36, 0, TimeSpan.Zero));
        string path = Path.Combine(_directory, "ratings.db");
        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        var failingProvider = new FakeProvider(result: null);
        var failingService = new AllocineRatingCacheService(store, failingProvider, NullLogger<AllocineRatingCacheService>.Instance, time);

        Assert.Null(await failingService.GetRatingsAsync(Request(), CancellationToken.None));
        Assert.Equal(1, failingProvider.Calls);

        var successProvider = new FakeProvider(new Dictionary<string, string> { ["public"] = "2.4" });
        var retryService = new AllocineRatingCacheService(store, successProvider, NullLogger<AllocineRatingCacheService>.Instance, time);
        Dictionary<string, string>? ratings = await retryService.GetRatingsAsync(
            Request() with { AllocineId = "1000020435" },
            CancellationToken.None);

        Assert.Equal("2.4", ratings?["public"]);
        Assert.Equal(1, successProvider.Calls);
    }

    [Fact]
    public async Task FailedRetryWithKnownAllocineIdUsesBackoff()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 25, 10, 36, 0, TimeSpan.Zero));
        string path = Path.Combine(_directory, "ratings.db");
        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        var failingProvider = new FakeProvider(result: null);
        var service = new AllocineRatingCacheService(store, failingProvider, NullLogger<AllocineRatingCacheService>.Instance, time);

        Assert.Null(await service.GetRatingsAsync(Request(), CancellationToken.None));
        Assert.Null(await service.GetRatingsAsync(
            Request() with { AllocineId = "1000020435" },
            CancellationToken.None));
        Assert.Equal(2, failingProvider.Calls);

        Assert.Null(await service.GetRatingsAsync(
            Request() with { AllocineId = "1000020435" },
            CancellationToken.None));
        Assert.Equal(2, failingProvider.Calls);
    }

    [Fact]
    public async Task FailedLookupRetriesWhenAFreshMappingAppears()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 25, 10, 36, 0, TimeSpan.Zero));
        string path = Path.Combine(_directory, "ratings.db");
        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        var failingService = new AllocineRatingCacheService(
            store,
            new FakeProvider(result: null),
            NullLogger<AllocineRatingCacheService>.Instance,
            time);

        Assert.Null(await failingService.GetRatingsAsync(Request(), CancellationToken.None));

        await store.WriteMappingAsync(
            Request(),
            "1000012412",
            time.GetUtcNow(),
            AllocineResolutionSource.ExactIdentifiers,
            CancellationToken.None);

        var mappedProvider = new SplitFakeProvider("1000012412", new Dictionary<string, string> { ["public"] = "2.0" });
        var retryService = new AllocineRatingCacheService(store, mappedProvider, NullLogger<AllocineRatingCacheService>.Instance, time);
        Dictionary<string, string>? ratings = await retryService.GetRatingsAsync(Request(), CancellationToken.None);

        Assert.Equal("2.0", ratings?["public"]);
        Assert.Equal(1, mappedProvider.RatingCalls);
        Assert.Equal(0, mappedProvider.ResolveCalls);
    }

    [Fact]
    public async Task AdaptiveFreshnessRefreshesNewReleasesMoreOftenThanCatalogMovies()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        var provider = new FakeProvider(new Dictionary<string, string> { ["public"] = "4.0" });
        var store = new AllocineRatingStore(Path.Combine(_directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
        var service = new AllocineRatingCacheService(store, provider, NullLogger<AllocineRatingCacheService>.Instance, time);
        AllocineRatingsRequest newRelease = Request(year: 2026, itemId: "new-release") with { AllocineId = "1001" };
        AllocineRatingsRequest catalogMovie = Request(year: 2010, itemId: "catalog-movie") with { AllocineId = "1002" };

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

        Assert.Null(entry);
        await using var migrated = new SqliteConnection($"Data Source={path}");
        await migrated.OpenAsync();
        await using SqliteCommand version = migrated.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(6L, await version.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task VersionTwoOrThreeMigrationDropsTransientFailuresButKeepsRatings(int priorVersion)
    {
        string path = Path.Combine(_directory, "ratings.db");
        AllocineRatingsRequest rated = Request(tmdbId: "123", itemId: "0123456789abcdef0123456789abcdef") with { AllocineId = "190918" };
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
        Assert.Equal(6L, await version.ExecuteScalarAsync());
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
            command.CommandText = "PRAGMA user_version=7;";
            await command.ExecuteNonQueryAsync();
        }

        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ReadAsync(Request(), CancellationToken.None));
        Assert.Contains("newer than supported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SchemaFiveDatabaseMigratesRatingsOntoTheAllocineIdKey()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "ratings.db");
        AllocineRatingsRequest preserved = Request(itemId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") with { AllocineId = "190918" };
        AllocineRatingsRequest otherItem = Request(itemId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", tmdbId: "999") with { AllocineId = "190918" };
        AllocineRatingsRequest idLessMiss = Request(itemId: "cccccccccccccccccccccccccccccccc", tmdbId: "456");
        AllocineRatingsRequest knownIdMiss = Request(itemId: "dddddddddddddddddddddddddddddddd", tmdbId: "789") with { AllocineId = "1000020435" };
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
                    consecutive_failures INTEGER NOT NULL DEFAULT 0,
                    allocine_id TEXT NULL
                );
                CREATE TABLE mappings (
                    identity_key TEXT PRIMARY KEY NOT NULL,
                    media_type TEXT NOT NULL,
                    imdb_id TEXT NULL,
                    tmdb_id TEXT NULL,
                    allocine_id TEXT NOT NULL,
                    resolved_utc TEXT NOT NULL,
                    source TEXT NOT NULL DEFAULT 'ExactIdentifiers'
                );
                CREATE TABLE native_writes (
                    jellyfin_item_id TEXT PRIMARY KEY NOT NULL,
                    allocine_id TEXT NOT NULL,
                    identity_key TEXT NOT NULL,
                    written_utc TEXT NOT NULL
                );
                INSERT INTO ratings VALUES (
                    'jellyfin:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', $identity_key, 'Movie',
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'tt1234567', '123',
                    'Example', 'Example', 2024, 1, '{"public":"4.2"}', $fetched_utc,
                    $fetched_utc, 0, '190918');
                INSERT INTO ratings VALUES (
                    'jellyfin:cccccccccccccccccccccccccccccccc', $miss_identity, 'Movie',
                    'cccccccccccccccccccccccccccccccc', 'tt1234567', '456',
                    'Example', 'Example', 2024, 0, NULL, $fetched_utc,
                    $fetched_utc, 3, NULL);
                INSERT INTO ratings VALUES (
                    'jellyfin:dddddddddddddddddddddddddddddddd', $known_miss_identity, 'Movie',
                    'dddddddddddddddddddddddddddddddd', 'tt36073210', '789',
                    'Example', 'Example', 2024, 0, NULL, $fetched_utc,
                    $fetched_utc, 3, '1000020435');
                INSERT INTO mappings VALUES (
                    $identity_key, 'Movie', 'tt1234567', '123', '190918', $fetched_utc, 'ExactIdentifiers');
                INSERT INTO native_writes VALUES (
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', '190918', $identity_key, $fetched_utc);
                PRAGMA user_version=5;
                """;
            command.Parameters.AddWithValue("$identity_key", AllocineRatingStore.IdentityKey(preserved));
            command.Parameters.AddWithValue("$miss_identity", AllocineRatingStore.IdentityKey(idLessMiss));
            command.Parameters.AddWithValue("$known_miss_identity", AllocineRatingStore.IdentityKey(knownIdMiss));
            command.Parameters.AddWithValue("$fetched_utc", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        AllocineCacheEntry? shared = await store.ReadAsync(otherItem, CancellationToken.None);
        AllocineCacheEntry? dropped = await store.ReadAsync(idLessMiss, CancellationToken.None);
        AllocineCacheEntry? droppedKnownMiss = await store.ReadAsync(knownIdMiss, CancellationToken.None);
        AllocineMappingEntry? mapping = await store.ReadMappingAsync(preserved, CancellationToken.None);
        AllocineNativeWriteEntry? provenance = await store.ReadNativeWriteAsync(preserved.ItemId, CancellationToken.None);

        Assert.Equal("4.2", shared?.Ratings?["public"]);
        Assert.Equal("190918", shared?.AllocineId);
        Assert.Null(dropped);
        Assert.Null(droppedKnownMiss);
        Assert.Equal("190918", mapping?.AllocineId);
        Assert.Equal("190918", provenance?.AllocineId);
        await using var migrated = new SqliteConnection($"Data Source={path}");
        await migrated.OpenAsync();
        await using SqliteCommand version = migrated.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(6L, await version.ExecuteScalarAsync());
        await using SqliteCommand keys = migrated.CreateCommand();
        keys.CommandText = "SELECT rating_key FROM ratings ORDER BY rating_key;";
        await using SqliteDataReader reader = await keys.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("MOVIE|id:190918", reader.GetString(0));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task SchemaFiveProductionCopyMigratesPositiveAllocineIdRows()
    {
        string? source = Environment.GetEnvironmentVariable("ALLOCINE_SCHEMA5_DB");
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "production.db");
        File.Copy(source, path, overwrite: true);
        var store = new AllocineRatingStore(path, NullLogger<AllocineRatingStore>.Instance);
        AllocineRatingsRequest sample = Request() with { AllocineId = "190918" };
        _ = await store.ReadAsync(sample, CancellationToken.None);

        await using var migrated = new SqliteConnection($"Data Source={path}");
        await migrated.OpenAsync();
        await using SqliteCommand version = migrated.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(6L, await version.ExecuteScalarAsync());
        await using SqliteCommand count = migrated.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM ratings WHERE found=1 AND allocine_id IS NOT NULL;";
        Assert.True((long)(await count.ExecuteScalarAsync())! > 0);
        await using SqliteCommand checkpoint = migrated.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await checkpoint.ExecuteNonQueryAsync();
        string? output = Environment.GetEnvironmentVariable("ALLOCINE_SCHEMA5_OUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            File.Copy(path, output, overwrite: true);
        }
    }

    [Fact]
    public async Task TwoItemsWithTheSameAllocineIdShareOneCachedRating()
    {
        var provider = new FakeProvider(new Dictionary<string, string> { ["public"] = "4.2" });
        var store = new AllocineRatingStore(Path.Combine(_directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
        var service = new AllocineRatingCacheService(store, provider, NullLogger<AllocineRatingCacheService>.Instance);
        AllocineRatingsRequest first = Request(itemId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") with { AllocineId = "190918" };
        AllocineRatingsRequest second = Request(itemId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", tmdbId: "999") with { AllocineId = "190918" };

        Assert.Equal("4.2", (await service.GetRatingsAsync(first, CancellationToken.None))?["public"]);
        var secondProvider = new FakeProvider(throwOnCall: true);
        var secondService = new AllocineRatingCacheService(store, secondProvider, NullLogger<AllocineRatingCacheService>.Instance);
        Dictionary<string, string>? shared = await secondService.GetRatingsAsync(second, CancellationToken.None);

        Assert.Equal("4.2", shared?["public"]);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(0, secondProvider.Calls);
    }

    [Fact]
    public async Task NativeAllocineIdIsUsedForRatingsWithoutReResolvingAnExpiredMapping()
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
        Assert.Equal("111", provider.RatedAllocineId);
        Assert.Equal(0, provider.ExactCalls);
        AllocineMappingEntry? mapping = await store.ReadMappingAsync(request, CancellationToken.None);
        Assert.Equal("111", mapping?.AllocineId);
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

        Assert.Equal(AllocineRefreshResult.Updated, outcome.Result);
        Assert.Equal("4.8", outcome.Ratings?["public"]);
        AllocineMappingEntry? mapping = await store.ReadMappingAsync(request, CancellationToken.None);
        Assert.Equal("111", mapping?.AllocineId);
        Assert.Equal(AllocineResolutionSource.ExactIdentifiers, mapping?.Source);
        Assert.Equal(0, provider.ExactCalls);
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
        Assert.Equal(0, provider.ExactCalls);
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
