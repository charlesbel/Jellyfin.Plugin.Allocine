namespace Jellyfin.Plugin.Allocine.Tests;

public sealed class AllocineRatingsParserTests
{
    private const string RatingsHtml = """
        <div class="rating-holder rating-holder-3">
          <div class="rating-item">
            <div class="rating-item-content">
              <span class="opaque-link rating-title"> Presse </span>
              <div class="stareval"><span class="stareval-note">3,6</span><span>40 critiques</span></div>
            </div>
          </div>
          <div class="rating-item">
            <div class="rating-item-content">
              <span class="opaque-link rating-title"> Spectateurs </span>
              <div class="stareval"><span class="stareval-note">4,5</span><span>34392 notes</span></div>
            </div>
          </div>
        </div>
        """;

    [Fact]
    public void ParseReturnsPressAndAudienceRatingsFromPublicMoviePage()
    {
        Dictionary<string, string>? ratings = AllocineRatingsParser.Parse(RatingsHtml);

        Assert.NotNull(ratings);
        Assert.Equal("3.6", ratings["presse"]);
        Assert.Equal("4.5", ratings["public"]);
    }

    [Fact]
    public void ParseDoesNotBorrowScoreFromFollowingRatingBlock()
    {
        const string html = """
            <div class="rating-item-content"><span class="rating-title"> Presse </span></div>
            <div class="rating-item-content"><span class="rating-title"> Spectateurs </span>
            <span class="stareval-note">4,5</span></div>
            """;

        Dictionary<string, string>? ratings = AllocineRatingsParser.Parse(html);

        Assert.NotNull(ratings);
        Assert.False(ratings.ContainsKey("presse"));
        Assert.Equal("4.5", ratings["public"]);
    }

    [Theory]
    [InlineData("<html><title>Just a moment...</title><div id='cf-chl-widget'></div></html>")]
    [InlineData("<html><script src='/challenge-platform/h/g/orchestrate/chl_page/v1'></script></html>")]
    [InlineData("<html><h1>Attention Required</h1></html>")]
    [InlineData("<html><h1>Allociné</h1><p>No rating blocks</p></html>")]
    public void ParseRejectsChallengeAndMalformedPages(string html)
    {
        Assert.Null(AllocineRatingsParser.Parse(html));
    }
}
