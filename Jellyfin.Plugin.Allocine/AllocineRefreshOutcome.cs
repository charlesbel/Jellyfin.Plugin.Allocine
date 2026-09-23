using System.Collections.Generic;

namespace Jellyfin.Plugin.Allocine
{
    internal enum AllocineRefreshResult
    {
        Skipped,
        Updated,
        Failed,
    }

    internal readonly record struct AllocineRefreshOutcome(
        AllocineRefreshResult Result,
        Dictionary<string, string>? Ratings);
}
