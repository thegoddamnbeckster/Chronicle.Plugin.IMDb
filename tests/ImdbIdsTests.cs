using FluentAssertions;
using Xunit;

namespace Chronicle.Plugin.IMDb.Tests;

/// <summary>Fix Match input: every IMDb link shape in PLUGIN_IMDB.md §10.5.</summary>
public class ImdbIdsTests
{
    [Theory]
    [InlineData("https://www.imdb.com/title/tt0133093/", "imdb:tt0133093")]
    [InlineData("https://www.imdb.com/title/tt0133093", "imdb:tt0133093")]
    [InlineData("https://www.imdb.com/title/tt0133093/?ref_=nv_sr_srsg_0", "imdb:tt0133093")]
    [InlineData("https://www.imdb.com/title/tt0133093/fullcredits", "imdb:tt0133093")]
    [InlineData("https://www.imdb.com/title/tt0133093/reviews?ref_=tt_ov_rt", "imdb:tt0133093")]
    [InlineData("https://www.imdb.com/title/tt0903747/episodes/?season=2", "imdb:tt0903747")]
    [InlineData("https://www.imdb.com/de/title/tt0133093/", "imdb:tt0133093")]
    [InlineData("https://m.imdb.com/title/tt0133093/", "imdb:tt0133093")]
    [InlineData("https://pro.imdb.com/title/tt0133093/", "imdb:tt0133093")]
    [InlineData("imdb.com/title/tt2301451/", "imdb:tt2301451")]
    [InlineData("https://www.imdb.com/name/nm0000206/", "imdb:nm0000206")]
    [InlineData("https://www.imdb.com/name/nm0000206/bio?ref_=nm_ov_bio_sm", "imdb:nm0000206")]
    [InlineData("tt0133093", "imdb:tt0133093")]
    [InlineData("  TT0133093 ", "imdb:tt0133093")]
    [InlineData("imdb:tt0133093", "imdb:tt0133093")]
    [InlineData("nm0000206", "imdb:nm0000206")]
    [InlineData("imdb:nm0000206", "imdb:nm0000206")]
    [InlineData("imdb:tt0903747/season:5", "imdb:tt0903747/season:5")]
    [InlineData("tt12345678", "imdb:tt12345678")]
    public void Parse_AcceptsEveryLinkAndIdForm(string input, string expected) =>
        ImdbIds.Parse(input).ToExternalId().Should().Be(expected);

    [Theory]
    [InlineData("https://www.imdb.com/list/ls055592025/", "list")]
    [InlineData("https://www.imdb.com/user/ur12345678/ratings", "list")]
    [InlineData("https://www.imdb.com/company/co0002663/", "list")]
    [InlineData("https://www.imdb.com/event/ev0000003/", "list")]
    [InlineData("https://www.imdb.com/find/?q=matrix", "list")]
    [InlineData("https://www.imdb.com/search/title/?genres=action", "list")]
    [InlineData("https://www.themoviedb.org/movie/603", "not an imdb.com link")]
    [InlineData("https://www.imdb.com/title/nm0000206/", "Couldn't find")]
    [InlineData("movie:603", "not an IMDb id")]
    [InlineData("", "Enter an IMDb link")]
    [InlineData("nm0000206/season:2", "person id with a season")]
    public void Parse_RejectsWithAClearMessage(string input, string messageFragment)
    {
        var act = () => ImdbIds.Parse(input);
        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain(messageFragment);
    }

    [Fact]
    public void TitleNumber_ReadsStoredAndCrossReferencedForms()
    {
        ImdbIds.TitleNumber("tt0903747").Should().Be(903747);
        ImdbIds.TitleNumber("tt0903747/season:5").Should().Be(903747);
        ImdbIds.TitleNumber("imdb:tt0903747").Should().Be(903747);
        ImdbIds.TitleNumber("imdb:nm0000206").Should().BeNull();
        ImdbIds.TitleNumber(null).Should().BeNull();
    }
}
