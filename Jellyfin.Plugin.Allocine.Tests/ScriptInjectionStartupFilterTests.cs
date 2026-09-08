namespace Jellyfin.Plugin.Allocine.Tests;

public sealed class ScriptInjectionStartupFilterTests
{
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

        Assert.Contains("<script src=\"/Allocine/Script\" defer></script>\n</body>", once, StringComparison.Ordinal);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void InjectScriptLeavesFragmentsWithoutBodyUnchanged()
    {
        const string html = "<main>not an index document</main>";

        Assert.Equal(html, ScriptInjectionStartupFilter.InjectScript(html));
    }
}
