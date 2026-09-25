using System;

namespace Jellyfin.Plugin.Allocine
{
    internal sealed record AllocineNativeWriteEntry(
        string JellyfinItemId,
        string AllocineId,
        string IdentityKey,
        DateTimeOffset WrittenAt);
}
