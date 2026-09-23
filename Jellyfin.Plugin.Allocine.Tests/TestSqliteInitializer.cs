using System.Runtime.CompilerServices;

namespace Jellyfin.Plugin.Allocine.Tests;

internal static class TestSqliteInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        SQLitePCL.Batteries_V2.Init();
    }
}
