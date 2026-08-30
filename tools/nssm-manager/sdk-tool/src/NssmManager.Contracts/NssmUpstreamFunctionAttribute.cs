namespace NssmManager.Contracts;

/// <summary>
/// Binds one managed method to one concrete function definition in the
/// NSSM 2.24-101-g897c7ad source tree.  The source-map generator treats this
/// attribute as executable translation evidence; file-level mappings are not
/// accepted.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class NssmUpstreamFunctionAttribute : Attribute
{
    public NssmUpstreamFunctionAttribute(
        string source,
        int line,
        string signature,
        string verification)
    {
        Source = source;
        Line = line;
        Signature = signature;
        Verification = verification;
    }

    public string Source { get; }
    public int Line { get; }
    public string Signature { get; }
    public string Verification { get; }

    /// <summary>
    /// True only for functions from gui.cpp whose Win32 dialog implementation
    /// is intentionally represented by the Avalonia surface.
    /// </summary>
    public bool FrontendRewrite { get; init; }
}
