namespace Jellyfin.Plugin.Allocine
{
    /// <summary>
    /// Result of resolving one media identity to an AlloCiné identifier.
    /// </summary>
    /// <param name="AllocineId">The proven identifier, when exactly one exists.</param>
    /// <param name="Source">How the identifier was obtained.</param>
    /// <param name="IsConflict">Whether supplied identifiers contradicted each other or failed closed.</param>
    public sealed record AllocineResolvedIdentity(
        string? AllocineId,
        AllocineResolutionSource Source,
        bool IsConflict);
}
