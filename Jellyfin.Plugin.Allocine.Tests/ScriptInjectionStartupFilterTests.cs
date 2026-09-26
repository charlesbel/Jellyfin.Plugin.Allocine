namespace Jellyfin.Plugin.Allocine.Tests;

public sealed class ScriptInjectionStartupFilterTests
{
    [Fact]
    public void AssemblyVersionMatchesInjectedScriptCacheBuster()
    {
        Assert.Equal(new Version(0, 6, 0, 0), typeof(ScriptInjectionStartupFilter).Assembly.GetName().Version);
    }

    [Theory]
    [InlineData("/web", true)]
    [InlineData("/web/", true)]
    [InlineData("/web/index.html", true)]
    [InlineData("/jellyfin/web/index.html", true)]
    [InlineData("/web/index.html/asset.js", false)]
    [InlineData("/Items", false)]
    [InlineData(null, false)]
    public void IsIndexRequestRecognizesOnlyJellyfinWebEntryPoints(string? path, bool expected)
    {
        Assert.Equal(expected, ScriptInjectionStartupFilter.IsIndexRequest(path));
    }

    [Fact]
    public void InjectScriptAddsTheAllocineEndpointBeforeBodyOnce()
    {
        const string html = "<html><body><main>Jellyfin</main></body></html>";

        string once = ScriptInjectionStartupFilter.InjectScript(html);
        string twice = ScriptInjectionStartupFilter.InjectScript(once);

        Assert.Contains("<script src=\"/Allocine/Script?v=0.6.0\" defer></script>\n</body>", once, StringComparison.Ordinal);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void InjectScriptUsesConfiguredPathBase()
    {
        const string html = "<html><body></body></html>";
        var method = typeof(ScriptInjectionStartupFilter).GetMethod(
            "InjectScript",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
            [typeof(string), typeof(string)]);

        Assert.NotNull(method);
        string injected = Assert.IsType<string>(method.Invoke(null, [html, "/jellyfin"]));
        Assert.Contains("src=\"/jellyfin/Allocine/Script?v=0.6.0\"", injected, StringComparison.Ordinal);
    }

    [Fact]
    public void InjectScriptLeavesFragmentsWithoutBodyUnchanged()
    {
        const string html = "<main>not an index document</main>";

        Assert.Equal(html, ScriptInjectionStartupFilter.InjectScript(html));
    }
}
