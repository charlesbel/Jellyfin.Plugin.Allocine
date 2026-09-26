using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Controller to access Allocine data.
    /// </summary>
    [ApiController]
    [Route("Allocine")]
    public class AllocineController : ControllerBase
    {
        private readonly AllocineRatingCacheService _ratingCacheService;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="AllocineController"/> class.
        /// </summary>
        /// <param name="ratingCacheService">The cache-first rating service.</param>
        /// <param name="libraryManager">Jellyfin's supported library abstraction.</param>
        /// <param name="userManager">Jellyfin's user manager.</param>
        public AllocineController(
            AllocineRatingCacheService ratingCacheService,
            ILibraryManager libraryManager,
            IUserManager userManager)
        {
            _ratingCacheService = ratingCacheService;
            _libraryManager = libraryManager;
            _userManager = userManager;
        }

        /// <summary>
        /// Serves the embedded JavaScript file.
        /// </summary>
        /// <returns>The JavaScript file.</returns>
        [HttpGet("Script")]
        [Produces("application/javascript")]
        public ActionResult GetScript()
        {
            var assembly = typeof(AllocineController).Assembly;
            string resourceName = "Jellyfin.Plugin.Allocine.allocine.js";

            var stream = assembly.GetManifestResourceStream(resourceName);

            if (stream == null)
            {
                return NotFound();
            }

            return File(stream, "application/javascript");
        }

        /// <summary>
        /// Serves an official AlloCiné editorial badge SVG.
        /// </summary>
        /// <param name="name">The badge identifier: <c>classiques</c>, <c>club-aime</c>, <c>les-indes</c>, or <c>club-scream</c>.</param>
        /// <returns>The SVG file, or not found.</returns>
        [HttpGet("Badge/{name}")]
        [Produces("image/svg+xml")]
        public ActionResult GetBadge(string name)
        {
            string? resourceName = name switch
            {
                "classiques" => "Jellyfin.Plugin.Allocine.Badges.classiques.svg",
                "club-aime" => "Jellyfin.Plugin.Allocine.Badges.club-aime.svg",
                "les-indes" => "Jellyfin.Plugin.Allocine.Badges.les-indes.svg",
                "club-scream" => "Jellyfin.Plugin.Allocine.Badges.club-scream.svg",
                _ => null,
            };
            if (resourceName == null)
            {
                return NotFound();
            }

            var stream = typeof(AllocineController).Assembly.GetManifestResourceStream(resourceName);
            return stream == null ? NotFound() : File(stream, "image/svg+xml");
        }

        /// <summary>
        /// Gets the ratings for a movie or series.
        /// </summary>
        /// <param name="itemId">The stable Jellyfin item identifier.</param>
        /// <param name="cancellationToken">The request cancellation token.</param>
        /// <returns>A JSON object containing the ratings.</returns>
        [HttpGet("Ratings")]
        [Authorize]
        [Produces("application/json")]
        public async Task<ActionResult<object>> GetRatings(
            [FromQuery] Guid itemId,
            CancellationToken cancellationToken = default)
        {
            if (itemId == Guid.Empty)
            {
                return BadRequest(new { message = "A valid Jellyfin item ID is required" });
            }

            Guid? currentUserId = GetCurrentUserId(User);
            if (currentUserId == null)
            {
                return Forbid();
            }

            var currentUser = _userManager.GetUserById(currentUserId.Value);
            if (currentUser == null)
            {
                return Forbid();
            }

            BaseItem? item = _libraryManager.GetItemById<BaseItem>(itemId, currentUser);
            if (item == null || !AllocineRefreshTask.TryCreateRequest(item, out AllocineRatingsRequest request))
            {
                return NotFound(new { message = "Supported Jellyfin media item not found" });
            }

            var ratings = await _ratingCacheService
                .GetRatingsAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (ratings == null)
            {
                return NotFound(new { message = "Media not found" });
            }

            return Ok(ratings);
        }

        internal static Guid? GetCurrentUserId(ClaimsPrincipal principal)
        {
            string? value = principal.Claims
                .FirstOrDefault(claim => claim.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))
                ?.Value;
            return Guid.TryParse(value, out Guid userId) ? userId : null;
        }
    }
}
