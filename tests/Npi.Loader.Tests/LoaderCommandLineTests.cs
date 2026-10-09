namespace Npi.Loader.Tests;

public class LoaderCommandLineTests
{
    [Fact]
    public void No_arguments_means_run()
    {
        var invocation = LoaderCommandLine.Parse([], out var error);

        Assert.Null(error);
        Assert.NotNull(invocation);
        Assert.Equal(LoaderCommand.Run, invocation.Command);
        Assert.Empty(invocation.Arguments);
    }

    [Theory]
    [InlineData("run", LoaderCommand.Run)]
    [InlineData("migrate", LoaderCommand.Migrate)]
    [InlineData("discover", LoaderCommand.Discover)]
    [InlineData("reference", LoaderCommand.Reference)]
    [InlineData("project", LoaderCommand.Project)]
    [InlineData("publish", LoaderCommand.Publish)]
    [InlineData("DISCOVER", LoaderCommand.Discover)]
    public void Known_commands_parse(string name, LoaderCommand expected)
    {
        var invocation = LoaderCommandLine.Parse([name], out var error);

        Assert.Null(error);
        Assert.Equal(expected, invocation?.Command);
    }

    [Fact]
    public void Load_file_takes_the_zip_path()
    {
        var invocation = LoaderCommandLine.Parse(["load-file", @"C:\work\NPPES_Data_Dissemination_100626_101226_Weekly_V2.zip"], out var error);

        Assert.Null(error);
        Assert.Equal(LoaderCommand.LoadFile, invocation?.Command);
        Assert.Equal([@"C:\work\NPPES_Data_Dissemination_100626_101226_Weekly_V2.zip"], invocation?.Arguments);
    }

    [Fact]
    public void Datasets_takes_an_optional_source_name()
    {
        Assert.Equal([], LoaderCommandLine.Parse(["datasets"], out _)?.Arguments);
        var one = LoaderCommandLine.Parse(["datasets", "oig_leie"], out var error);
        Assert.Null(error);
        Assert.Equal(LoaderCommand.Datasets, one?.Command);
        Assert.Equal(["oig_leie"], one?.Arguments);
    }

    [Fact]
    public void Geocode_takes_an_optional_county_fips()
    {
        Assert.Equal([], LoaderCommandLine.Parse(["geocode"], out _)?.Arguments);
        Assert.Equal(["36103"], LoaderCommandLine.Parse(["geocode", "36103"], out _)?.Arguments);
        Assert.Null(LoaderCommandLine.Parse(["geocode", "Suffolk"], out var error));
        Assert.Contains("FIPS", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Overture_takes_a_state_a_county_or_nothing()
    {
        Assert.Equal([], LoaderCommandLine.Parse(["overture"], out _)?.Arguments);
        Assert.Equal(["NY"], LoaderCommandLine.Parse(["overture", "ny"], out _)?.Arguments);
        Assert.Equal(["36103"], LoaderCommandLine.Parse(["overture", "36103"], out _)?.Arguments);
        Assert.Null(LoaderCommandLine.Parse(["overture", "Suffolk"], out var error));
        Assert.Contains("state code", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("datasets", "a", "b")]
    [InlineData("geocode", "36103", "36059")]
    [InlineData("load-file")]
    [InlineData("load-file", "a.zip", "b.zip")]
    [InlineData("run", "extra")]
    [InlineData("bogus")]
    public void Invalid_command_lines_are_rejected(params string[] args)
    {
        var invocation = LoaderCommandLine.Parse(args, out var error);

        Assert.Null(invocation);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
