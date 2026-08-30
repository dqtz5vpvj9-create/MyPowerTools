namespace NssmManager.Supervisor;

internal static class NssmFileOptions
{
    public static FileMode Mode(uint creationDisposition) => creationDisposition switch
    {
        1 => FileMode.CreateNew,
        2 => FileMode.Create,
        3 => FileMode.Open,
        4 => FileMode.OpenOrCreate,
        5 => FileMode.Truncate,
        _ => throw new ArgumentOutOfRangeException(nameof(creationDisposition), creationDisposition, "Unsupported Win32 creation disposition.")
    };

    public static FileShare Share(uint shareMode)
    {
        if ((shareMode & ~7u) != 0) throw new ArgumentOutOfRangeException(nameof(shareMode), shareMode, "Unsupported Win32 share mode.");
        return (FileShare)shareMode;
    }

    public static FileOptions Options(uint flagsAndAttributes)
    {
        var result = FileOptions.None;
        if ((flagsAndAttributes & 0x80000000u) != 0) result |= FileOptions.WriteThrough;
        if ((flagsAndAttributes & 0x40000000u) != 0) result |= FileOptions.Asynchronous;
        if ((flagsAndAttributes & 0x10000000u) != 0) result |= FileOptions.RandomAccess;
        if ((flagsAndAttributes & 0x08000000u) != 0) result |= FileOptions.SequentialScan;
        if ((flagsAndAttributes & 0x04000000u) != 0) result |= FileOptions.DeleteOnClose;
        return result;
    }
}
