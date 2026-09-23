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
                returnsNull ? null : new Dictionary<string, string> { ["public"] = "4.0" });
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
