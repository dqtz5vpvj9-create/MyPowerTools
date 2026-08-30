using NssmManager.Supervisor;

namespace NssmManager.Tests;

public sealed class IoCompatibilityTests
{
    [Theory]
    [InlineData(1u, FileMode.CreateNew)]
    [InlineData(2u, FileMode.Create)]
    [InlineData(3u, FileMode.Open)]
    [InlineData(4u, FileMode.OpenOrCreate)]
    [InlineData(5u, FileMode.Truncate)]
    public void Creation_disposition_matches_CreateFile(uint value, FileMode expected) => Assert.Equal(expected, NssmFileOptions.Mode(value));

    [Theory]
    [InlineData(0u, FileShare.None)]
    [InlineData(1u, FileShare.Read)]
    [InlineData(3u, FileShare.ReadWrite)]
    [InlineData(7u, FileShare.ReadWrite | FileShare.Delete)]
    public void Share_mode_matches_CreateFile(uint value, FileShare expected) => Assert.Equal(expected, NssmFileOptions.Share(value));

}
