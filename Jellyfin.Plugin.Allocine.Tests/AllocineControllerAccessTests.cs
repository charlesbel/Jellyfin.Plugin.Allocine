using System.Security.Claims;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.Allocine.Tests;

public sealed class AllocineControllerAccessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"allocine-controller-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task HiddenItemReturnsNotFoundThroughUserScopedLookup()
    {
        Guid userId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        var user = new User("viewer", "auth", "reset") { Id = userId };
        var userManager = new Mock<IUserManager>();
        userManager.Setup(manager => manager.GetUserById(userId)).Returns(user);
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(manager => manager.GetItemById<BaseItem>(itemId, user)).Returns((BaseItem?)null);
        libraryManager.Setup(manager => manager.GetItemById<BaseItem>(itemId)).Throws(new InvalidOperationException("Unscoped lookup must not be used."));
        AllocineController controller = CreateController(libraryManager.Object, userManager.Object, userId);

        ActionResult<object> result = await controller.GetRatings(itemId, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        libraryManager.Verify(manager => manager.GetItemById<BaseItem>(itemId, user), Times.Once);
        libraryManager.Verify(manager => manager.GetItemById<BaseItem>(itemId), Times.Never);
    }

    [Fact]
    public async Task VisibleMovieCanUseTheCacheFirstPipeline()
    {
        Guid userId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        var user = new User("viewer", "auth", "reset") { Id = userId };
        var movie = new Movie { Id = itemId, Name = "Visible", OriginalTitle = "Visible", ProductionYear = 2024 };
        var userManager = new Mock<IUserManager>();
        userManager.Setup(manager => manager.GetUserById(userId)).Returns(user);
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(manager => manager.GetItemById<BaseItem>(itemId, user)).Returns(movie);
        AllocineController controller = CreateController(libraryManager.Object, userManager.Object, userId);

        ActionResult<object> result = await controller.GetRatings(itemId, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        var ratings = Assert.IsType<Dictionary<string, string>>(ok.Value);
        Assert.Equal("4.4", ratings["public"]);
    }

    [Fact]
    public async Task AuthenticationWithoutAUserIdentityIsForbidden()
    {
        var userManager = new Mock<IUserManager>(MockBehavior.Strict);
        var libraryManager = new Mock<ILibraryManager>(MockBehavior.Strict);
        AllocineController controller = CreateController(libraryManager.Object, userManager.Object, userId: null);

        ActionResult<object> result = await controller.GetRatings(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private AllocineController CreateController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        Guid? userId)
    {
        var store = new AllocineRatingStore(Path.Combine(_directory, "ratings.db"), NullLogger<AllocineRatingStore>.Instance);
        var cache = new AllocineRatingCacheService(
            store,
            new FixedProvider(),
            NullLogger<AllocineRatingCacheService>.Instance);
        var controller = new AllocineController(cache, libraryManager, userManager);
        IEnumerable<Claim> claims = userId.HasValue
            ? new[] { new Claim("Jellyfin-UserId", userId.Value.ToString("N")) }
            : new[] { new Claim(ClaimTypes.Role, "Administrator") };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Jellyfin")),
            },
        };
        return controller;
    }

    private sealed class FixedProvider : IAllocineRatingProvider
    {
        public Task<Dictionary<string, string>?> GetRatingsAsync(
            AllocineRatingsRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<Dictionary<string, string>?>(new Dictionary<string, string> { ["public"] = "4.4" });
        }
    }
}
