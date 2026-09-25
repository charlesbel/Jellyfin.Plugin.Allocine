using System;

namespace Jellyfin.Plugin.Allocine
{
    internal sealed record AllocineMappingEntry(
        string IdentityKey,
        string AllocineId,
        DateTimeOffset ResolvedAt,
        AllocineResolutionSource Source = AllocineResolutionSource.ExactIdentifiers);
}
