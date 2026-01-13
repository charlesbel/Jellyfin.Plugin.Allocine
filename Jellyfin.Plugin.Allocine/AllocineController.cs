using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
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
        private readonly AllocineService _allocineService;

        /// <summary>
        /// Initializes a new instance of the <see cref="AllocineController"/> class.
        /// </summary>
        /// <param name="allocineService">The Allocine service.</param>
        public AllocineController(AllocineService allocineService)
        {
            _allocineService = allocineService;
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
        /// Gets the ratings for a movie.
        /// </summary>
        /// <param name="title">The movie title.</param>
        /// <param name="year">The production year.</param>
        /// <returns>A JSON object containing the ratings.</returns>
        [HttpGet("Ratings")]
        [Produces("application/json")]
        public async Task<ActionResult<object>> GetRatings([FromQuery, Required] string title, [FromQuery] int year)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return BadRequest(new { message = "Title is missing" });
            }

            var ratings = await _allocineService.GetRatings(title, year).ConfigureAwait(false);

            if (ratings == null)
            {
                return NotFound(new { message = "Movie not found" });
            }

            return Ok(ratings);
        }
    }
}
