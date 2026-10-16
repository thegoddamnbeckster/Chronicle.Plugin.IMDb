using Chronicle.Plugin.IMDb;
using Xunit;

namespace Chronicle.Plugin.IMDb.Tests;

public class ExternalIdPolicyTests
{
    [Fact]
    public void TheProviderDeclaresHowItsIdsBehave()
    {
        var p = new ImdbMetadataProvider().GetExternalIdPolicy();
        Assert.Equal("imdb", p.Source);
        Assert.Equal("imdb:", p.SharedRawIdPrefix);
        Assert.Contains("nm", p.KeepSharedPrefixWhenIdStarts);
        Assert.Equal("imdb:{id}", p.CrossRefFormats["default"]);
    }
}
