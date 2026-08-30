using NssmManager.Windows;

namespace NssmManager.Tests;

public sealed class NssmCliDifferentialTests
{
    [Fact]
    public void usage_matches_upstream()
    {
        using var output = new StringWriter();
        Assert.Equal(7, NssmCore.usage(7, output));
        Assert.Contains("NSSM: The non-sucking service manager", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Version 2.24-101-g897c7ad 64-bit, 2017-04-26", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void elevated_mutations_preserve_exit_codes()
    {
        var arguments = new[] { "install", "service" };
        Assert.Equal(111, NssmCore.elevate(arguments, received =>
        {
            Assert.Same(arguments, received);
            return 111;
        }));
    }
}
