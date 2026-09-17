using WinLocalASR.Core.Shell;
using WinLocalASR.Tests.State;
using Xunit;

namespace WinLocalASR.Tests.Shell;

public class CommandLineOptionsTests
{
    [Fact]
    public void Boolean_flags_are_recognized_and_unknown_flags_stay_false()
    {
        CommandLineOptions options = CommandLineOptions.Parse(new[] { "--enable-control-server" });

        Assert.True(options.HasFlag(CommandLineOptions.EnableControlServerFlag));
        Assert.False(options.HasFlag("something-else"));
    }

    [Fact]
    public void Key_value_pairs_parse_with_last_value_winning()
    {
        CommandLineOptions options = CommandLineOptions.Parse(new[] { "--foo=bar", "--k=1", "--k=2" });

        Assert.True(options.TryGetValue("foo", out string foo));
        Assert.Equal("bar", foo);
        Assert.True(options.TryGetValue("k", out string k));
        Assert.Equal("2", k);
        Assert.False(options.TryGetValue("missing", out _));
        Assert.False(options.HasFlag("foo")); // a value pair is not a flag
    }

    [Theory]
    [InlineData("plain-word")]
    [InlineData("--")]
    [InlineData("")]
    public void Non_flag_tokens_are_ignored(string token)
    {
        CommandLineOptions options = CommandLineOptions.Parse(new[] { token });

        Assert.False(options.HasFlag(token));
        Assert.False(options.TryGetValue(token.TrimStart('-'), out _));
    }

    [Fact]
    public void Task12_flag_names_are_pinned()
    {
        Assert.Equal("enable-control-server", CommandLineOptions.EnableControlServerFlag);
        Assert.Equal("fake-configured", CommandLineOptions.FakeConfiguredFlag);
    }

    [Fact]
    public async Task Bootstrapper_exposes_the_parsed_flag_bag()
    {
        AppBootstrapper bootstrapper = ShellTestHost.CreateBootstrapper(
            new FakeAppServiceGraphFactory(new AppServiceGraph(new ControllerHarness().Controller, true)),
            out _);

        int exitCode = await bootstrapper.RunAsync(new[] { "--enable-control-server", "--future=on" });

        Assert.Equal(0, exitCode);
        Assert.True(bootstrapper.Options.HasFlag(CommandLineOptions.EnableControlServerFlag));
        Assert.True(bootstrapper.Options.TryGetValue("future", out string value));
        Assert.Equal("on", value);
    }
}
