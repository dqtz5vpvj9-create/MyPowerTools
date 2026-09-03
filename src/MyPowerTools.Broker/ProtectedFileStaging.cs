using System.Security.Cryptography;

namespace MyPowerTools.Broker;

/// <summary>
/// Decides whether a file staged into an ACL-protected location actually has to be replaced.
///
/// A shared service host executable is image-locked for as long as any service it hosts is
/// running, so replacing it unconditionally turns every install or migration into a sharing
/// violation the moment one managed service is up. Byte-identical content needs no replacement.
/// </summary>
public static class ProtectedFileStaging
{
    /// <summary>
    /// True when <paramref name="destinationPath"/> already holds content hashing to
    /// <paramref name="expectedSha256"/>. A destination that is absent, locked against reads or
    /// different returns false, which leaves the caller on its normal stage-and-replace path.
    /// </summary>
    public static bool AlreadyMatches(string destinationPath, ReadOnlySpan<byte> expectedSha256)
    {
        if (!File.Exists(destinationPath))
        {
            return false;
        }

        try
        {
            using var destination = new FileStream(
                destinationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(destination), expectedSha256);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
