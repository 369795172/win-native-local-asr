using Xunit;

namespace WinLocalASR.Tests;

public class SmokeTests
{
    // Bootstrap smoke test: proves the solution restores, builds, and runs tests
    // on both macOS (local) and windows-latest (CI) before real coverage lands.
    [Fact]
    public void Test_infrastructure_executes()
    {
        Assert.True(true);
    }
}
