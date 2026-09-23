using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Allocine
{
    internal sealed record AllocineCacheEntry(
        string IdentityKey,
        bool Found,
        Dictionary<string, string>? Ratings,
        DateTimeOffset FetchedAt,
        DateTimeOffset? LastAttemptAt,
        int ConsecutiveFailures);
}
