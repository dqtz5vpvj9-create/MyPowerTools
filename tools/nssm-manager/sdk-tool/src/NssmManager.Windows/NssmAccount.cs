using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using NssmManager.Contracts;

namespace NssmManager.Windows;

/// <summary>Direct managed translation of account.cpp.</summary>
public static class NssmAccount
{
    public const string LocalSystemAccount = "LocalSystem";
    public const string LocalServiceAccount = @"NT Authority\LocalService";
    public const string NetworkServiceAccount = @"NT Authority\NetworkService";
    public const string VirtualServiceAccountDomain = "NT Service";
    public const string LogonAsServiceRight = "SeServiceLogonRight";

    private const uint PolicyAllAccess = 0x000F0FFF;
    private const int SidTypeUser = 1;
    private const int SidTypeWellKnownGroup = 5;
    private const int SidTypeUnknown = 8;
    private const int ErrorFileNotFound = 2;

    [NssmUpstreamFunction("src/account.cpp", 12, "int open_lsa_policy(LSA_HANDLE *policy)", "NssmAccountTests.open_lsa_policy_returns_upstream_status")]
    public static int open_lsa_policy(out LsaPolicyHandle? policy)
    {
        policy = null;
        if (!OperatingSystem.IsWindows()) return 1;
        var attributes = new LsaObjectAttributes();
        var status = LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyAllAccess, out var rawPolicy);
        if (status != 0)
        {
            NssmEvent.print_message(Console.Error, NssmEvent.message_id("NSSM_MESSAGE_LSAOPENPOLICY_FAILED"), NssmEvent.error_string(LsaNtStatusToWinError(status)));
            return 1;
        }
        policy = new LsaPolicyHandle(rawPolicy);
        return 0;
    }

    [NssmUpstreamFunction("src/account.cpp", 26, "int username_sid(const TCHAR *username, SID **sid, LSA_HANDLE *policy)", "NssmAccountTests.username_sid_matches_lsa_lookup_names")]
    public static int username_sid(string username, out SecurityIdentifier? sid, LsaPolicyHandle? policy)
    {
        sid = null;
        LsaPolicyHandle? ownedPolicy = null;
        if (policy is null)
        {
            if (open_lsa_policy(out ownedPolicy) != 0) return 1;
            policy = ownedPolicy;
        }

        try
        {
            string expanded;
            try
            {
                expanded = username.StartsWith(@".\", StringComparison.OrdinalIgnoreCase)
                    ? $@"{Environment.MachineName}\{username[2..]}"
                    : new string(username.AsSpan());
            }
            catch (OutOfMemoryException)
            {
                return 2;
            }

            using var lsaUsername = LsaUnicodeString.Create(expanded);
            var status = LsaLookupNames(policy!.DangerousGetHandle(), 1, [lsaUsername.Value], out var translatedDomains, out var translatedSids);
            try
            {
                if (status != 0) return 5;
                if (translatedDomains == IntPtr.Zero || translatedSids == IntPtr.Zero) return 7;

                var translatedSid = Marshal.PtrToStructure<LsaTranslatedSid>(translatedSids);
                if (translatedSid.Use != SidTypeUser && translatedSid.Use != SidTypeWellKnownGroup)
                {
                    var virtualPrefix = VirtualServiceAccountDomain + @"\";
                    if (translatedSid.Use != SidTypeUnknown || !username.StartsWith(virtualPrefix, StringComparison.OrdinalIgnoreCase)) return 6;
                }

                var domains = Marshal.PtrToStructure<LsaReferencedDomainList>(translatedDomains);
                if (translatedSid.DomainIndex < 0 || checked((uint)translatedSid.DomainIndex) >= domains.Entries) return 7;
                var trustPointer = IntPtr.Add(domains.Domains, checked(translatedSid.DomainIndex * Marshal.SizeOf<LsaTrustInformation>()));
                var trust = Marshal.PtrToStructure<LsaTrustInformation>(trustPointer);
                if (trust.Sid == IntPtr.Zero || !IsValidSid(trust.Sid)) return 7;

                try
                {
                    var domainSid = CopySid(trust.Sid);
                    sid = new SecurityIdentifier($"{domainSid.Value}-{translatedSid.RelativeId}");
                }
                catch (OutOfMemoryException)
                {
                    return 8;
                }
                catch (ArgumentException)
                {
                    sid = null;
                    return 9;
                }

                if (translatedSid.Use == SidTypeWellKnownGroup && well_known_sid(sid) is null)
                {
                    sid = null;
                    return 10;
                }
                return 0;
            }
            finally
            {
                if (translatedDomains != IntPtr.Zero) LsaFreeMemory(translatedDomains);
                if (translatedSids != IntPtr.Zero) LsaFreeMemory(translatedSids);
            }
        }
        finally
        {
            ownedPolicy?.Dispose();
        }
    }

    [NssmUpstreamFunction("src/account.cpp", 148, "int username_sid(const TCHAR *username, SID **sid)", "NssmAccountTests.username_sid_matches_lsa_lookup_names")]
    public static int username_sid(string username, out SecurityIdentifier? sid) => username_sid(username, out sid, null);

    [NssmUpstreamFunction("src/account.cpp", 152, "int canonicalise_username(const TCHAR *username, TCHAR **canon)", "NssmAccountTests.canonicalise_username_matches_lsa_lookup_sids")]
    public static int canonicalise_username(string username, out string? canonical)
    {
        canonical = null;
        if (open_lsa_policy(out var policy) != 0 || policy is null) return 1;
        using (policy)
        {
            if (username_sid(username, out var sid, policy) != 0 || sid is null) return 2;
            var bytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(bytes, 0);
            var sidMemory = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, sidMemory, bytes.Length);
            try
            {
                var status = LsaLookupSids(policy.DangerousGetHandle(), 1, [sidMemory], out var translatedDomains, out var translatedNames);
                try
                {
                    if (status != 0) return 3;
                    if (translatedDomains == IntPtr.Zero || translatedNames == IntPtr.Zero) return 3;

                    var translatedName = Marshal.PtrToStructure<LsaTranslatedName>(translatedNames);
                    var domains = Marshal.PtrToStructure<LsaReferencedDomainList>(translatedDomains);
                    if (translatedName.DomainIndex < 0 || checked((uint)translatedName.DomainIndex) >= domains.Entries) return 3;
                    var trustPointer = IntPtr.Add(domains.Domains, checked(translatedName.DomainIndex * Marshal.SizeOf<LsaTrustInformation>()));
                    var trust = Marshal.PtrToStructure<LsaTrustInformation>(trustPointer);
                    try
                    {
                        canonical = $"{ReadLsaString(trust.Name)}\\{ReadLsaString(translatedName.Name)}";
                        return 0;
                    }
                    catch (OutOfMemoryException)
                    {
                        canonical = null;
                        return 9;
                    }
                }
                finally
                {
                    if (translatedDomains != IntPtr.Zero) LsaFreeMemory(translatedDomains);
                    if (translatedNames != IntPtr.Zero) LsaFreeMemory(translatedNames);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(sidMemory);
            }
        }
    }

    [NssmUpstreamFunction("src/account.cpp", 203, "int username_equiv(const TCHAR *a, const TCHAR *b)", "NssmAccountTests.username_equiv_compares_sids")]
    public static int username_equiv(string a, string b)
    {
        if (username_sid(a, out var sidA) != 0 || sidA is null) return 0;
        if (username_sid(b, out var sidB) != 0 || sidB is null) return 0;
        return sidA.Equals(sidB) ? 1 : 0;
    }

    [NssmUpstreamFunction("src/account.cpp", 222, "int is_localsystem(const TCHAR *username)", "NssmAccountTests.is_localsystem_accepts_alias_and_sid_name")]
    public static int is_localsystem(string username)
    {
        if (NssmCore.str_equiv(username, LocalSystemAccount) != 0) return 1;
        if (username_sid(username, out var sid) != 0 || sid is null) return 0;
        return sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ? 1 : 0;
    }

    [NssmUpstreamFunction("src/account.cpp", 238, "TCHAR *virtual_account(const TCHAR *service_name)", "NssmAccountTests.virtual_account_uses_nt_service_domain")]
    public static string? virtual_account(string serviceName)
    {
        try
        {
            return $@"{VirtualServiceAccountDomain}\{serviceName}";
        }
        catch (OutOfMemoryException)
        {
            return null;
        }
    }

    [NssmUpstreamFunction("src/account.cpp", 251, "int is_virtual_account(const TCHAR *service_name, const TCHAR *username)", "NssmAccountTests.is_virtual_account_is_case_insensitive")]
    public static int is_virtual_account(string? serviceName, string? username)
    {
        if (serviceName is null || username is null) return 0;
        var canonical = virtual_account(serviceName);
        return canonical is not null && NssmCore.str_equiv(canonical, username) != 0 ? 1 : 0;
    }

    [NssmUpstreamFunction("src/account.cpp", 266, "const TCHAR *well_known_sid(SID *sid)", "NssmAccountTests.well_known_sid_returns_nssm_aliases")]
    public static string? well_known_sid(SecurityIdentifier sid)
    {
        if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid)) return LocalSystemAccount;
        if (sid.IsWellKnown(WellKnownSidType.LocalServiceSid)) return LocalServiceAccount;
        if (sid.IsWellKnown(WellKnownSidType.NetworkServiceSid)) return NetworkServiceAccount;
        return null;
    }

    [NssmUpstreamFunction("src/account.cpp", 274, "const TCHAR *well_known_username(const TCHAR *username)", "NssmAccountTests.well_known_username_defaults_to_localsystem")]
    public static string? well_known_username(string? username)
    {
        if (username is null) return LocalSystemAccount;
        if (NssmCore.str_equiv(username, LocalSystemAccount) != 0) return LocalSystemAccount;
        return username_sid(username, out var sid) == 0 && sid is not null ? well_known_sid(sid) : null;
    }

    [NssmUpstreamFunction("src/account.cpp", 286, "int grant_logon_as_service(const TCHAR *username)", "NssmAccountTests.grant_logon_as_service_matches_lsa_right_enumeration")]
    public static int grant_logon_as_service(string? username)
    {
        if (username is null) return 0;
        if (open_lsa_policy(out var policy) != 0 || policy is null) return 1;
        using (policy)
        {
            if (username_sid(username, out var sid, policy) != 0 || sid is null) return 2;
            if (well_known_sid(sid) is not null) return 3;

            var sidBytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(sidBytes, 0);
            var sidPointer = Marshal.AllocHGlobal(sidBytes.Length);
            Marshal.Copy(sidBytes, 0, sidPointer, sidBytes.Length);
            try
            {
                var status = LsaEnumerateAccountRights(policy.DangerousGetHandle(), sidPointer, out var rights, out var count);
                if (status != 0)
                {
                    var error = LsaNtStatusToWinError(status);
                    if (error != ErrorFileNotFound) return 4;
                    rights = IntPtr.Zero;
                    count = 0;
                }

                try
                {
                    for (var index = 0u; index < count; index++)
                    {
                        var pointer = IntPtr.Add(rights, checked((int)index * Marshal.SizeOf<LsaUnicodeStringValue>()));
                        var right = Marshal.PtrToStructure<LsaUnicodeStringValue>(pointer);
                        if (string.Equals(ReadLsaString(right), LogonAsServiceRight, StringComparison.OrdinalIgnoreCase)) return 0;
                    }
                }
                finally
                {
                    if (rights != IntPtr.Zero) LsaFreeMemory(rights);
                }

                using var requestedRight = LsaUnicodeString.Create(LogonAsServiceRight);
                status = LsaAddAccountRights(policy.DangerousGetHandle(), sidPointer, [requestedRight.Value], 1);
                return status == 0 ? 0 : 5;
            }
            finally
            {
                Marshal.FreeHGlobal(sidPointer);
            }
        }
    }

    private static SecurityIdentifier CopySid(IntPtr sid)
    {
        var length = checked((int)GetLengthSid(sid));
        var bytes = new byte[length];
        Marshal.Copy(sid, bytes, 0, length);
        return new SecurityIdentifier(bytes, 0);
    }

    private static string ReadLsaString(LsaUnicodeStringValue value) =>
        value.Buffer == IntPtr.Zero || value.Length == 0
            ? string.Empty
            : Marshal.PtrToStringUni(value.Buffer, value.Length / sizeof(char)) ?? string.Empty;

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeStringValue
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    private sealed class LsaUnicodeString : IDisposable
    {
        private LsaUnicodeString(IntPtr buffer, LsaUnicodeStringValue value)
        {
            Buffer = buffer;
            Value = value;
        }

        private IntPtr Buffer { get; set; }
        public LsaUnicodeStringValue Value { get; }

        public static LsaUnicodeString Create(string value)
        {
            var buffer = Marshal.StringToHGlobalUni(value);
            return new LsaUnicodeString(buffer, new LsaUnicodeStringValue
            {
                Length = checked((ushort)(value.Length * sizeof(char))),
                MaximumLength = checked((ushort)((value.Length + 1) * sizeof(char))),
                Buffer = buffer
            });
        }

        public void Dispose()
        {
            if (Buffer == IntPtr.Zero) return;
            Marshal.FreeHGlobal(Buffer);
            Buffer = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaReferencedDomainList
    {
        public uint Entries;
        public IntPtr Domains;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaTrustInformation
    {
        public LsaUnicodeStringValue Name;
        public IntPtr Sid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaTranslatedSid
    {
        public int Use;
        public uint RelativeId;
        public int DomainIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaTranslatedName
    {
        public int Use;
        public LsaUnicodeStringValue Name;
        public int DomainIndex;
    }

    public sealed class LsaPolicyHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal LsaPolicyHandle(IntPtr handle) : base(ownsHandle: true) => SetHandle(handle);
        protected override bool ReleaseHandle() => LsaClose(handle) == 0;
    }

    [DllImport("advapi32.dll")]
    private static extern uint LsaOpenPolicy(IntPtr systemName, ref LsaObjectAttributes objectAttributes, uint desiredAccess, out IntPtr policyHandle);

    [DllImport("advapi32.dll")]
    private static extern uint LsaLookupNames(IntPtr policyHandle, uint count, [In] LsaUnicodeStringValue[] names, out IntPtr referencedDomains, out IntPtr sids);

    [DllImport("advapi32.dll")]
    private static extern uint LsaLookupSids(IntPtr policyHandle, uint count, [In] IntPtr[] sids, out IntPtr referencedDomains, out IntPtr names);

    [DllImport("advapi32.dll")]
    private static extern uint LsaEnumerateAccountRights(IntPtr policyHandle, IntPtr accountSid, out IntPtr userRights, out uint countOfRights);

    [DllImport("advapi32.dll")]
    private static extern uint LsaAddAccountRights(IntPtr policyHandle, IntPtr accountSid, [In] LsaUnicodeStringValue[] userRights, uint countOfRights);

    [DllImport("advapi32.dll")]
    private static extern uint LsaFreeMemory(IntPtr buffer);

    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr policyHandle);

    [DllImport("advapi32.dll")]
    private static extern uint LsaNtStatusToWinError(uint status);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSid(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern uint GetLengthSid(IntPtr sid);
}
