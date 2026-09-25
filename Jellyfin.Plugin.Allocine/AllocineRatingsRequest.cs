namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Immutable media identity used by the rating cache and remote provider.
    /// </summary>
    public sealed record AllocineRatingsRequest(
        string ItemId,
        string MediaType,
        string Title,
        string? OriginalTitle,
        int Year,
        string? ImdbId,
        string? TmdbId,
        string? AllocineId = null);
}
