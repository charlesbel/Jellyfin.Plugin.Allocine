using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.Allocine.Tests;

public sealed class AllocineRefreshTaskTests
{
    [Fact]
    public void TaskIsDiscoverableAndRunsDaily()
    {
        Assert.True(typeof(IScheduledTask).IsAssignableFrom(typeof(AllocineRefreshTask)));

        TaskTriggerInfo trigger = Assert.Single(AllocineRefreshTask.DefaultTriggers());
        Assert.Equal(TaskTriggerInfoType.DailyTrigger, trigger.Type);
        Assert.Equal(TimeSpan.FromHours(3).Ticks, trigger.TimeOfDayTicks);
    }

    [Fact]
    public void CreatesExactRequestsForMoviesAndSeries()
    {
        var movie = new Movie
        {
            Id = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
            Name = "Film",
            OriginalTitle = "Movie",
            ProductionYear = 2024,
        };
        movie.SetProviderId(MetadataProvider.Imdb, "tt1234567");
        movie.SetProviderId(MetadataProvider.Tmdb, "123");

        var series = new Series
        {
            Id = Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210"),
            Name = "Série",
            OriginalTitle = "Series",
            ProductionYear = 2023,
        };
        series.SetProviderId(MetadataProvider.Imdb, "tt7654321");
        series.SetProviderId(MetadataProvider.Tmdb, "456");

        Assert.True(AllocineRefreshTask.TryCreateRequest(movie, out AllocineRatingsRequest? movieRequest));
        Assert.True(AllocineRefreshTask.TryCreateRequest(series, out AllocineRatingsRequest? seriesRequest));
        Assert.Equal(BaseItemKind.Movie.ToString(), movieRequest!.MediaType);
        Assert.Equal("tt1234567", movieRequest.ImdbId);
        Assert.Equal(BaseItemKind.Series.ToString(), seriesRequest!.MediaType);
        Assert.Equal("456", seriesRequest.TmdbId);
    }

    [Fact]
    public void SkipsItemsWithoutTheMetadataRequiredByTheExistingSafeMatcher()
    {
        var movie = new Movie { Name = "Unknown", ProductionYear = null };

        Assert.False(AllocineRefreshTask.TryCreateRequest(movie, out _));
    }

    [Fact]
    public void ScheduledRefreshSkipsItemsWithoutStableProviderIdsButDynamicFallbackKeepsThem()
    {
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "A legitimate title-only movie",
            OriginalTitle = "A legitimate title-only movie",
            ProductionYear = 2024,
        };

        Assert.True(AllocineRefreshTask.TryCreateRequest(movie, out _));
        Assert.False(AllocineRefreshTask.TryCreateScheduledRequest(movie, out _));
    }

    [Fact]
    public async Task SecondRunSkipsAStillFreshCachedRating()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"allocine-task-tests-{Guid.NewGuid():N}");
        try
        {
            var movie = new Movie
            {
                Id = Guid.NewGuid(),
                Name = "Fresh movie",
                OriginalTitle = "Fresh movie",
                ProductionYear = DateTimeOffset.UtcNow.Year,
            };
            movie.SetProviderId(MetadataProvider.Imdb, "tt1234567");
            var library = new Mock<ILibraryManager>();
            library.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { movie });
            var provider = new CountingProvider();
            var store = new AllocineRatingStore(Path.Combine(directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
            var cache = new AllocineRatingCacheService(store, provider, NullLogger<AllocineRatingCacheService>.Instance);
            var task = new AllocineRefreshTask(
                library.Object,
                cache,
                NullLogger<AllocineRefreshTask>.Instance,
                TimeSpan.Zero);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            Assert.Equal(1, provider.Calls);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ScheduledRunWritesMissingNativeIdFromAFreshExactMappingWithoutRefreshingRatings()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"allocine-task-native-{Guid.NewGuid():N}");
        try
        {
            var movie = new Movie
            {
                Id = Guid.NewGuid(),
                Name = "Intouchables",
                OriginalTitle = "Intouchables",
                ProductionYear = 2011,
            };
            movie.SetProviderId(MetadataProvider.Imdb, "tt1675434");
            movie.SetProviderId(MetadataProvider.Tmdb, "77338");
            Assert.True(AllocineRefreshTask.TryCreateScheduledRequest(movie, out AllocineRatingsRequest request));

            var store = new AllocineRatingStore(Path.Combine(directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
            await store.WriteMappingAsync(
                request,
                "182745",
                DateTimeOffset.UtcNow,
                AllocineResolutionSource.ExactIdentifiers,
                CancellationToken.None);
            await store.WriteAsync(
                request with { AllocineId = "182745" },
                AllocineEditorialFlags.WithDefaults(new Dictionary<string, string> { ["public"] = "4.4", ["press"] = "3.7" }),
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            var library = new Mock<ILibraryManager>();
            library.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { movie });
            library.Setup(manager => manager.UpdateItemAsync(
                    It.IsAny<BaseItem>(),
                    It.IsAny<BaseItem>(),
                    ItemUpdateType.MetadataImport,
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var mapping = new ScheduledMappingProvider { ExactId = "182745" };
            var cache = new AllocineRatingCacheService(store, mapping, NullLogger<AllocineRatingCacheService>.Instance);
            var task = new AllocineRefreshTask(
                library.Object,
                cache,
                NullLogger<AllocineRefreshTask>.Instance,
                TimeSpan.Zero);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            Assert.Equal("182745", movie.GetProviderId(AllocineProviderNames.Key));
            Assert.Equal(0, mapping.ExactCalls);
            Assert.Equal(0, mapping.RatingCalls);
            library.Verify(
                manager => manager.UpdateItemAsync(
                    movie,
                    It.IsAny<BaseItem>(),
                    ItemUpdateType.MetadataImport,
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ScheduledRunDoesNotRecordNativeWriteWhenLibraryPersistFails()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"allocine-task-persist-fail-{Guid.NewGuid():N}");
        try
        {
            var movie = new Movie
            {
                Id = Guid.NewGuid(),
                Name = "Intouchables",
                OriginalTitle = "Intouchables",
                ProductionYear = 2011,
            };
            movie.SetProviderId(MetadataProvider.Imdb, "tt1675434");
            movie.SetProviderId(MetadataProvider.Tmdb, "77338");
            Assert.True(AllocineRefreshTask.TryCreateScheduledRequest(movie, out AllocineRatingsRequest request));

            var store = new AllocineRatingStore(Path.Combine(directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
            await store.WriteMappingAsync(
                request,
                "182745",
                DateTimeOffset.UtcNow,
                AllocineResolutionSource.ExactIdentifiers,
                CancellationToken.None);
            await store.WriteAsync(
                request,
                new Dictionary<string, string> { ["public"] = "4.4" },
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            var library = new Mock<ILibraryManager>();
            library.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { movie });
            library.Setup(manager => manager.UpdateItemAsync(
                    It.IsAny<BaseItem>(),
                    It.IsAny<BaseItem>(),
                    ItemUpdateType.MetadataImport,
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("library persist failed"));
            var mapping = new ScheduledMappingProvider { ExactId = "182745" };
            var cache = new AllocineRatingCacheService(store, mapping, NullLogger<AllocineRatingCacheService>.Instance);
            var task = new AllocineRefreshTask(
                library.Object,
                cache,
                NullLogger<AllocineRefreshTask>.Instance,
                TimeSpan.Zero);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            Assert.Null(await store.ReadNativeWriteAsync(request.ItemId, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ScheduledRunDoesNotReplaceAPluginOwnedNativeIdAfterMappingExpiry()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"allocine-task-180d-{Guid.NewGuid():N}");
        try
        {
            var movie = new Movie
            {
                Id = Guid.NewGuid(),
                Name = "Intouchables",
                OriginalTitle = "Intouchables",
                ProductionYear = 2011,
            };
            movie.SetProviderId(MetadataProvider.Imdb, "tt1675434");
            movie.SetProviderId(MetadataProvider.Tmdb, "77338");
            movie.TrySetProviderId(AllocineProviderNames.Key, "111");
            Assert.True(AllocineRefreshTask.TryCreateScheduledRequest(movie, out AllocineRatingsRequest request));

            var store = new AllocineRatingStore(Path.Combine(directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await store.WriteMappingAsync(
                request,
                "111",
                now.AddDays(-181),
                AllocineResolutionSource.ExactIdentifiers,
                CancellationToken.None);
            await store.RecordNativeWriteAsync(
                request.ItemId,
                "111",
                AllocineRatingStore.IdentityKey(request),
                now.AddDays(-181),
                CancellationToken.None);
            await store.WriteAsync(
                request,
                AllocineEditorialFlags.WithDefaults(new Dictionary<string, string> { ["public"] = "1.0" }),
                now,
                CancellationToken.None);

            var library = new Mock<ILibraryManager>();
            library.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { movie });
            library.Setup(manager => manager.UpdateItemAsync(
                    It.IsAny<BaseItem>(),
                    It.IsAny<BaseItem>(),
                    ItemUpdateType.MetadataImport,
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var mapping = new ScheduledMappingProvider
            {
                ExactId = "222",
                Ratings = new Dictionary<string, string> { ["public"] = "4.8" },
            };
            var cache = new AllocineRatingCacheService(store, mapping, NullLogger<AllocineRatingCacheService>.Instance);
            var task = new AllocineRefreshTask(
                library.Object,
                cache,
                NullLogger<AllocineRefreshTask>.Instance,
                TimeSpan.Zero);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            Assert.Equal("111", movie.GetProviderId(AllocineProviderNames.Key));
            AllocineMappingEntry? persisted = await store.ReadMappingAsync(request, CancellationToken.None);
            Assert.Equal("111", persisted?.AllocineId);
            Assert.Equal(AllocineResolutionSource.ExactIdentifiers, persisted?.Source);
            Dictionary<string, string>? ratings = await cache.GetRatingsAsync(
                request with { AllocineId = "111" },
                CancellationToken.None);
            Assert.Equal("1.0", ratings?["public"]);
            Assert.Equal(0, mapping.ExactCalls);
            library.Verify(
                manager => manager.UpdateItemAsync(
                    movie,
                    It.IsAny<BaseItem>(),
                    ItemUpdateType.MetadataImport,
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ScheduledRunDoesNotReplaceAUserOwnedNativeIdAfterMappingExpiry()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"allocine-task-user-{Guid.NewGuid():N}");
        try
        {
            var movie = new Movie
            {
                Id = Guid.NewGuid(),
                Name = "Intouchables",
                OriginalTitle = "Intouchables",
                ProductionYear = 2011,
            };
            movie.SetProviderId(MetadataProvider.Imdb, "tt1675434");
            movie.SetProviderId(MetadataProvider.Tmdb, "77338");
            movie.TrySetProviderId(AllocineProviderNames.Key, "555");
            Assert.True(AllocineRefreshTask.TryCreateScheduledRequest(movie, out AllocineRatingsRequest request));

            var store = new AllocineRatingStore(Path.Combine(directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
            await store.WriteMappingAsync(
                request,
                "111",
                DateTimeOffset.UtcNow.AddDays(-181),
                AllocineResolutionSource.ExactIdentifiers,
                CancellationToken.None);
            await store.WriteAsync(
                request,
                new Dictionary<string, string> { ["public"] = "1.0" },
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            var library = new Mock<ILibraryManager>();
            library.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { movie });
            library.Setup(manager => manager.UpdateItemAsync(
                    It.IsAny<BaseItem>(),
                    It.IsAny<BaseItem>(),
                    ItemUpdateType.MetadataImport,
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var mapping = new ScheduledMappingProvider { ExactId = "222" };
            var cache = new AllocineRatingCacheService(store, mapping, NullLogger<AllocineRatingCacheService>.Instance);
            var task = new AllocineRefreshTask(
                library.Object,
                cache,
                NullLogger<AllocineRefreshTask>.Instance,
                TimeSpan.Zero);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            Assert.Equal("555", movie.GetProviderId(AllocineProviderNames.Key));
            AllocineMappingEntry? persisted = await store.ReadMappingAsync(request, CancellationToken.None);
            Assert.Equal("111", persisted?.AllocineId);
            Assert.Equal(0, mapping.ExactCalls);
            library.Verify(
                manager => manager.UpdateItemAsync(
                    It.IsAny<BaseItem>(),
                    It.IsAny<BaseItem>(),
                    It.IsAny<ItemUpdateType>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ScheduledRunDoesNotRetryAnExactMissOnAFreshUnknownMappingEveryDay()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"allocine-task-miss-{Guid.NewGuid():N}");
        try
        {
            var movie = new Movie
            {
                Id = Guid.NewGuid(),
                Name = "Obscure Title",
                OriginalTitle = "Obscure Title",
                ProductionYear = 2011,
            };
            movie.SetProviderId(MetadataProvider.Imdb, "tt1234567");
            movie.SetProviderId(MetadataProvider.Tmdb, "123");
            Assert.True(AllocineRefreshTask.TryCreateScheduledRequest(movie, out AllocineRatingsRequest request));

            var store = new AllocineRatingStore(Path.Combine(directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
            await store.WriteMappingAsync(
                request,
                "999",
                DateTimeOffset.UtcNow,
                AllocineResolutionSource.Unknown,
                CancellationToken.None);
            await store.WriteAsync(
                request,
                new Dictionary<string, string> { ["public"] = "3.0" },
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            var library = new Mock<ILibraryManager>();
            library.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { movie });
            library.Setup(manager => manager.UpdateItemAsync(
                    It.IsAny<BaseItem>(),
                    It.IsAny<BaseItem>(),
                    ItemUpdateType.MetadataImport,
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var mapping = new ScheduledMappingProvider { ExactId = null };
            var cache = new AllocineRatingCacheService(store, mapping, NullLogger<AllocineRatingCacheService>.Instance);
            var task = new AllocineRefreshTask(
                library.Object,
                cache,
                NullLogger<AllocineRefreshTask>.Instance,
                TimeSpan.Zero);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            Assert.Null(movie.GetProviderId(AllocineProviderNames.Key));
            Assert.Equal(1, mapping.ExactCalls);
            AllocineMappingEntry? persisted = await store.ReadMappingAsync(request, CancellationToken.None);
            Assert.Equal("999", persisted?.AllocineId);
            Assert.Equal(AllocineResolutionSource.ExactMiss, persisted?.Source);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ScheduledRunPromotesAFreshUnknownMappingToANativeIdAfterExactProof()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"allocine-task-unknown-{Guid.NewGuid():N}");
        try
        {
            var movie = new Movie
            {
                Id = Guid.NewGuid(),
                Name = "Intouchables",
                OriginalTitle = "Intouchables",
                ProductionYear = 2011,
            };
            movie.SetProviderId(MetadataProvider.Imdb, "tt1675434");
            movie.SetProviderId(MetadataProvider.Tmdb, "77338");
            Assert.True(AllocineRefreshTask.TryCreateScheduledRequest(movie, out AllocineRatingsRequest request));

            var store = new AllocineRatingStore(Path.Combine(directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
            await store.WriteMappingAsync(
                request,
                "999",
                DateTimeOffset.UtcNow,
                AllocineResolutionSource.Unknown,
                CancellationToken.None);
            await store.WriteAsync(
                request,
                new Dictionary<string, string> { ["public"] = "4.4" },
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            var library = new Mock<ILibraryManager>();
            library.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { movie });
            library.Setup(manager => manager.UpdateItemAsync(
                    It.IsAny<BaseItem>(),
                    It.IsAny<BaseItem>(),
                    ItemUpdateType.MetadataImport,
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var mapping = new ScheduledMappingProvider { ExactId = "182745" };
            var cache = new AllocineRatingCacheService(store, mapping, NullLogger<AllocineRatingCacheService>.Instance);
            var task = new AllocineRefreshTask(
                library.Object,
                cache,
                NullLogger<AllocineRefreshTask>.Instance,
                TimeSpan.Zero);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            Assert.Equal("182745", movie.GetProviderId(AllocineProviderNames.Key));
            AllocineMappingEntry? persisted = await store.ReadMappingAsync(request, CancellationToken.None);
            Assert.Equal("182745", persisted?.AllocineId);
            Assert.Equal(AllocineResolutionSource.ExactIdentifiers, persisted?.Source);
            Assert.Equal(1, mapping.ExactCalls);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ScheduledRunSkipsNativeWritesWhenTheOptionIsDisabled()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"allocine-task-disabled-{Guid.NewGuid():N}");
        try
        {
            var movie = new Movie
            {
                Id = Guid.NewGuid(),
                Name = "Intouchables",
                OriginalTitle = "Intouchables",
                ProductionYear = 2011,
            };
            movie.SetProviderId(MetadataProvider.Imdb, "tt1675434");
            movie.SetProviderId(MetadataProvider.Tmdb, "77338");
            Assert.True(AllocineRefreshTask.TryCreateScheduledRequest(movie, out AllocineRatingsRequest request));

            var store = new AllocineRatingStore(Path.Combine(directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
            await store.WriteMappingAsync(
                request,
                "182745",
                DateTimeOffset.UtcNow,
                AllocineResolutionSource.ExactIdentifiers,
                CancellationToken.None);
            await store.WriteAsync(
                request,
                new Dictionary<string, string> { ["public"] = "4.4" },
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            var library = new Mock<ILibraryManager>();
            library.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { movie });
            var mapping = new ScheduledMappingProvider { ExactId = "182745" };
            var cache = new AllocineRatingCacheService(store, mapping, NullLogger<AllocineRatingCacheService>.Instance);
            var task = new AllocineRefreshTask(
                library.Object,
                cache,
                NullLogger<AllocineRefreshTask>.Instance,
                TimeSpan.Zero,
                new PluginConfiguration { WriteNativeAllocineIds = false });

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            Assert.Null(movie.GetProviderId(AllocineProviderNames.Key));
            library.Verify(
                manager => manager.UpdateItemAsync(
                    It.IsAny<BaseItem>(),
                    It.IsAny<BaseItem>(),
                    It.IsAny<ItemUpdateType>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ScheduledRunDoesNotFreezeAStaleExactMappingWhenExactResolveReturnsNone()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"allocine-task-keep-exact-{Guid.NewGuid():N}");
        try
        {
            var movie = new Movie
            {
                Id = Guid.NewGuid(),
                Name = "Intouchables",
                OriginalTitle = "Intouchables",
                ProductionYear = 2011,
            };
            movie.SetProviderId(MetadataProvider.Imdb, "tt1675434");
            movie.SetProviderId(MetadataProvider.Tmdb, "77338");
            movie.TrySetProviderId(AllocineProviderNames.Key, "111");
            Assert.True(AllocineRefreshTask.TryCreateScheduledRequest(movie, out AllocineRatingsRequest request));

            var store = new AllocineRatingStore(Path.Combine(directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
            await store.WriteMappingAsync(
                request,
                "111",
                DateTimeOffset.UtcNow.AddDays(-181),
                AllocineResolutionSource.ExactIdentifiers,
                CancellationToken.None);
            await store.RecordNativeWriteAsync(
                request.ItemId,
                "111",
                AllocineRatingStore.IdentityKey(request),
                DateTimeOffset.UtcNow.AddDays(-181),
                CancellationToken.None);
            await store.WriteAsync(
                request,
                new Dictionary<string, string> { ["public"] = "4.4" },
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            var library = new Mock<ILibraryManager>();
            library.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { movie });
            var mapping = new ScheduledMappingProvider { ExactId = null };
            var cache = new AllocineRatingCacheService(store, mapping, NullLogger<AllocineRatingCacheService>.Instance);
            var task = new AllocineRefreshTask(
                library.Object,
                cache,
                NullLogger<AllocineRefreshTask>.Instance,
                TimeSpan.Zero);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            Assert.Equal("111", movie.GetProviderId(AllocineProviderNames.Key));
            AllocineMappingEntry? persisted = await store.ReadMappingAsync(request, CancellationToken.None);
            Assert.Equal("111", persisted?.AllocineId);
            Assert.Equal(AllocineResolutionSource.ExactIdentifiers, persisted?.Source);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ScheduledRunDoesNotExactMissAnUnknownMappingOnTransientFailure()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"allocine-task-transient-{Guid.NewGuid():N}");
        try
        {
            var movie = new Movie
            {
                Id = Guid.NewGuid(),
                Name = "Intouchables",
                OriginalTitle = "Intouchables",
                ProductionYear = 2011,
            };
            movie.SetProviderId(MetadataProvider.Imdb, "tt1675434");
            movie.SetProviderId(MetadataProvider.Tmdb, "77338");
            Assert.True(AllocineRefreshTask.TryCreateScheduledRequest(movie, out AllocineRatingsRequest request));

            var store = new AllocineRatingStore(Path.Combine(directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
            await store.WriteMappingAsync(
                request,
                "999",
                DateTimeOffset.UtcNow,
                AllocineResolutionSource.Unknown,
                CancellationToken.None);
            await store.WriteAsync(
                request,
                new Dictionary<string, string> { ["public"] = "4.4" },
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            var library = new Mock<ILibraryManager>();
            library.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { movie });
            var mapping = new ScheduledMappingProvider { Transient = true };
            var cache = new AllocineRatingCacheService(store, mapping, NullLogger<AllocineRatingCacheService>.Instance);
            var task = new AllocineRefreshTask(
                library.Object,
                cache,
                NullLogger<AllocineRefreshTask>.Instance,
                TimeSpan.Zero);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            Assert.Null(movie.GetProviderId(AllocineProviderNames.Key));
            AllocineMappingEntry? persisted = await store.ReadMappingAsync(request, CancellationToken.None);
            Assert.Equal("999", persisted?.AllocineId);
            Assert.Equal(AllocineResolutionSource.Unknown, persisted?.Source);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CompletionCountersAccountForEveryExaminedItem()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"allocine-task-counters-{Guid.NewGuid():N}");
        try
        {
            var eligible = new Movie { Id = Guid.NewGuid(), Name = "Eligible", ProductionYear = 2026 };
            eligible.SetProviderId(MetadataProvider.Imdb, "tt1234567");
            var ineligible = new Movie { Id = Guid.NewGuid(), Name = "No IDs", ProductionYear = 2026 };
            var library = new Mock<ILibraryManager>();
            library.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { eligible, ineligible });
            var logger = new RecordingLogger<AllocineRefreshTask>();
            var provider = new CountingProvider(returnsNull: true);
            var store = new AllocineRatingStore(Path.Combine(directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
            var cache = new AllocineRatingCacheService(store, provider, NullLogger<AllocineRatingCacheService>.Instance);
            var task = new AllocineRefreshTask(library.Object, cache, logger, TimeSpan.Zero);

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            Assert.Contains(
                logger.Messages,
                message => message.Contains("0 refreshed, 1 failed/backed off, 1 skipped, 2 examined", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class CountingProvider(bool returnsNull = false) : IAllocineRatingProvider
    {
        public int Calls { get; private set; }

        public Task<Dictionary<string, string>?> GetRatingsAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<Dictionary<string, string>?>(
                returnsNull ? null : AllocineEditorialFlags.WithDefaults(new Dictionary<string, string> { ["public"] = "4.0" }));
        }
    }

    private sealed class ScheduledMappingProvider : IAllocineMappingProvider
    {
        public string? ExactId { get; set; }

        public int ExactCalls { get; private set; }

        public int RatingCalls { get; private set; }

        public bool Transient { get; set; }

        public Dictionary<string, string> Ratings { get; set; } = new() { ["public"] = "4.0" };

        public Task<AllocineResolvedIdentity> ResolveIdentityAsync(
            AllocineRatingsRequest request,
            bool allowTitleYearFallback,
            CancellationToken cancellationToken)
        {
            if (!allowTitleYearFallback)
            {
                ExactCalls++;
            }

            if (Transient)
            {
                return Task.FromResult(new AllocineResolvedIdentity(
                    null,
                    AllocineResolutionSource.None,
                    false,
                    true));
            }

            return Task.FromResult(
                ExactId == null
                    ? new AllocineResolvedIdentity(null, AllocineResolutionSource.None, false)
                    : new AllocineResolvedIdentity(ExactId, AllocineResolutionSource.ExactIdentifiers, false));
        }

        public async Task<string?> ResolveAllocineIdAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            return (await ResolveIdentityAsync(request, false, cancellationToken)).AllocineId;
        }

        public Task<Dictionary<string, string>?> GetRatingsByAllocineIdAsync(
            string allocineId,
            string mediaType,
            CancellationToken cancellationToken)
        {
            RatingCalls++;
            return Task.FromResult<Dictionary<string, string>?>(AllocineEditorialFlags.WithDefaults(Ratings));
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

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
