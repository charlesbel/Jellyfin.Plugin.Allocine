using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Allocine.Tests;

public sealed class AllocineExternalIdTests
{
    [Fact]
    public void MovieExternalIdDeclaresTheStableAllocineKey()
    {
        var externalId = new AllocineMovieExternalId();

        Assert.Equal("AlloCiné", externalId.ProviderName);
        Assert.Equal(AllocineProviderNames.Key, externalId.Key);
        Assert.Equal(ExternalIdMediaType.Movie, externalId.Type);
        Assert.True(externalId.Supports(new Movie()));
        Assert.False(externalId.Supports(new Series()));
        Assert.False(externalId.Supports(new Person()));
    }

    [Fact]
    public void SeriesExternalIdDeclaresTheSameStableAllocineKey()
    {
        var externalId = new AllocineSeriesExternalId();

        Assert.Equal("AlloCiné", externalId.ProviderName);
        Assert.Equal(AllocineProviderNames.Key, externalId.Key);
        Assert.Equal(ExternalIdMediaType.Series, externalId.Type);
        Assert.True(externalId.Supports(new Series()));
        Assert.False(externalId.Supports(new Movie()));
        Assert.False(externalId.Supports(new Person()));
    }

    [Fact]
    public void ExternalUrlProviderBuildsCanonicalMovieAndSeriesLinks()
    {
        var provider = new AllocineExternalUrlProvider();
        var movie = new Movie { Name = "Intouchables" };
        movie.SetProviderId(AllocineProviderNames.Key, "190918");
        var series = new Series { Name = "The Office" };
        series.SetProviderId(AllocineProviderNames.Key, "331");

        Assert.Equal("AlloCiné", provider.Name);
        Assert.Equal(
            ["https://www.allocine.fr/film/fichefilm_gen_cfilm=190918.html"],
            provider.GetExternalUrls(movie));
        Assert.Equal(
            ["https://www.allocine.fr/series/ficheserie_gen_cserie=331.html"],
            provider.GetExternalUrls(series));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("0123")]
    [InlineData("1234567890123")]
    public void ExternalUrlProviderOmitsMissingOrMalformedIds(string? allocineId)
    {
        var provider = new AllocineExternalUrlProvider();
        var movie = new Movie { Name = "Unknown" };
        if (allocineId != null)
        {
            movie.SetProviderId(AllocineProviderNames.Key, allocineId);
        }

        Assert.Empty(provider.GetExternalUrls(movie));
    }

    [Fact]
    public void ExternalUrlProviderDoesNotEmitLinksForUnsupportedItemTypes()
    {
        var person = new Person();
        person.SetProviderId(AllocineProviderNames.Key, "190918");

        Assert.Empty(new AllocineExternalUrlProvider().GetExternalUrls(person));
    }
}
