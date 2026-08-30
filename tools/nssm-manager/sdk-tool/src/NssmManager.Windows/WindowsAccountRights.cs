namespace NssmManager.Windows;

internal static class WindowsAccountRights
{
    public static void GrantLogOnAsService(string account)
    {
        if (string.IsNullOrWhiteSpace(account) || account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) || account.StartsWith(@"NT AUTHORITY\", StringComparison.OrdinalIgnoreCase) || account.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase)) return;
        var result = NssmAccount.grant_logon_as_service(account);
        if (result != 0) throw new InvalidOperationException($"grant_logon_as_service() failed with NSSM status {result}.");
    }
}
