using Npi.Core.Search;
using Npi.Web.Api;

namespace Npi.Web.Tests;

public class ApiMetaTests
{
    [Fact]
    public void Meta_has_a_plain_as_of_date_and_utc_times()
    {
        var meta = ApiMeta.From(new DataVersion(new DateTime(2026, 10, 4), "m.zip", "w.zip", "d.zip", "261", "2026Q2", "2026", 9_482_099,
            new DateTime(2026, 10, 8, 11, 8, 29), PublishedAt: null));

        Assert.Equal(new DateOnly(2026, 10, 4), meta.DataAsOf);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 11, 8, 29, TimeSpan.Zero), meta.ProjectedAt);
        Assert.Null(meta.PublishedAt);
        Assert.Equal(9_482_099, meta.ProviderCount);
    }
}
