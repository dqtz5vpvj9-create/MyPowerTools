using System.Runtime.InteropServices;

namespace LocalLagCleaner.MyPowerTools;

internal sealed record SystemHandleTypeProbeResult(
    IReadOnlyList<HandleTypeSnapshot> Rows,
    IReadOnlyList<FileHandleAccessSnapshot> FileAccessPatterns,
    IReadOnlyList<SystemFileHandleDescriptor> FileHandleSamples,
    ulong EnumeratedSystemHandles,
    ulong EnumeratedAllHandles,
    ulong UnmappedSystemHandles,
    int MappedTypeCount,
    int BufferBytes);

internal sealed record SystemFileHandleDescriptor(
    ulong HandleValue,
    uint GrantedAccess);

/// <summary>
/// Reads the system-wide handle table once and aggregates PID 4 entries by
/// object type. It deliberately avoids duplicating handles or querying object
/// names one-by-one because those operations are slow, require broader access,
/// and can block on specific object implementations.
/// </summary>
internal static class SystemHandleTypeProbe
{
    private const int SystemExtendedHandleInformation = 64;
    private const int ObjectTypesInformation = 3;
    private const int MaximumBufferBytes = 512 * 1024 * 1024;
    private const int HandleBufferInitialBytes = 64 * 1024 * 1024;
    private const int ObjectTypesBufferInitialBytes = 64 * 1024;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferOverflow = unchecked((int)0x80000005);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);

    public static SystemHandleTypeProbeResult Read(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "System handle type enumeration supports Windows only.");
        }

        if (!NativeLibrary.TryLoad("ntdll.dll", out var nativeModule))
        {
            throw new DllNotFoundException("ntdll.dll could not be loaded.");
        }

        try
        {
            var querySystemInformation = LoadExport<NtQuerySystemInformationDelegate>(
                nativeModule,
                "NtQuerySystemInformation");
            var queryObject = LoadExport<NtQueryObjectDelegate>(
                nativeModule,
                "NtQueryObject");
            var typeNames = ReadObjectTypeNames(queryObject);

            using var handleBuffer = QueryVariableBuffer(
                (IntPtr buffer, uint length, out uint returnedLength) =>
                    querySystemInformation(
                        SystemExtendedHandleInformation,
                        buffer,
                        length,
                        out returnedLength),
                HandleBufferInitialBytes,
                "NtQuerySystemInformation(SystemExtendedHandleInformation)");

            return AggregateHandles(
                handleBuffer,
                typeNames,
                cancellationToken);
        }
        finally
        {
            NativeLibrary.Free(nativeModule);
        }
    }

    private static Dictionary<ushort, string> ReadObjectTypeNames(
        NtQueryObjectDelegate queryObject)
    {
        using var buffer = QueryVariableBuffer(
            (IntPtr address, uint length, out uint returnedLength) =>
                queryObject(
                    IntPtr.Zero,
                    ObjectTypesInformation,
                    address,
                    length,
                    out returnedLength),
            ObjectTypesBufferInitialBytes,
            "NtQueryObject(ObjectTypesInformation)");

        var numberOfTypes = checked((uint)Marshal.ReadInt32(buffer.Pointer));
        if (numberOfTypes is 0 or > 512)
        {
            throw new InvalidDataException(
                $"Object type table reported an invalid type count: {numberOfTypes}.");
        }

        var result = new Dictionary<ushort, string>();
        var structureSize = Marshal.SizeOf<ObjectTypeInformation>();
        var current = AlignUp(
            checked(buffer.Pointer.ToInt64() + sizeof(uint)),
            IntPtr.Size);
        var bufferStart = buffer.Pointer.ToInt64();
        var bufferEnd = checked(bufferStart + buffer.Length);

        for (var index = 0U; index < numberOfTypes; index++)
        {
            if (current < bufferStart ||
                current > bufferEnd - structureSize)
            {
                throw new InvalidDataException(
                    $"Object type row {index} exceeds the returned buffer.");
            }

            var address = new IntPtr(current);
            var information = Marshal.PtrToStructure<ObjectTypeInformation>(
                address);
            var name = ReadUnicodeString(
                information.TypeName,
                bufferStart,
                bufferEnd);
            if (information.TypeIndex > 0 &&
                !string.IsNullOrWhiteSpace(name))
            {
                result[information.TypeIndex] = name;
            }

            var structureEnd = checked(current + structureSize);
            var stringEnd = information.TypeName.Buffer == IntPtr.Zero
                ? structureEnd
                : checked(
                    information.TypeName.Buffer.ToInt64() +
                    information.TypeName.MaximumLength);
            current = AlignUp(
                Math.Max(structureEnd, stringEnd),
                IntPtr.Size);
        }

        if (result.Count == 0)
        {
            throw new InvalidDataException(
                "Object type table did not expose any indexed type names.");
        }

        return result;
    }

    private static unsafe SystemHandleTypeProbeResult AggregateHandles(
        NativeBuffer buffer,
        IReadOnlyDictionary<ushort, string> typeNames,
        CancellationToken cancellationToken)
    {
        var headerBytes = checked(IntPtr.Size * 2);
        var entryBytes = Marshal.SizeOf<SystemHandleTableEntryInfoEx>();
        if (buffer.Length < headerBytes)
        {
            throw new InvalidDataException(
                "System handle table buffer is shorter than its header.");
        }

        var reportedCount = IntPtr.Size == 8
            ? *(ulong*)buffer.Pointer
            : *(uint*)buffer.Pointer;
        var availableEntries =
            (ulong)(buffer.Length - headerBytes) / (ulong)entryBytes;
        if (reportedCount > availableEntries)
        {
            throw new InvalidDataException(
                $"System handle table reported {reportedCount:n0} rows, but the buffer contains at most {availableEntries:n0}.");
        }

        var allCounts = new ulong[ushort.MaxValue + 1];
        var systemCounts = new ulong[ushort.MaxValue + 1];
        var fileTypeIndex = typeNames
            .FirstOrDefault(pair =>
                string.Equals(
                    pair.Value,
                    "File",
                    StringComparison.OrdinalIgnoreCase))
            .Key;
        var fileAccessCounts = new Dictionary<uint, ulong>();
        var entries = (SystemHandleTableEntryInfoEx*)
            ((byte*)buffer.Pointer + headerBytes);
        ulong systemTotal = 0;
        for (ulong index = 0; index < reportedCount; index++)
        {
            if ((index & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var entry = entries[index];
            var typeIndex = entry.ObjectTypeIndex;
            allCounts[typeIndex]++;
            if (entry.UniqueProcessId.ToUInt64() == 4)
            {
                systemCounts[typeIndex]++;
                systemTotal++;
                if (typeIndex == fileTypeIndex)
                {
                    fileAccessCounts[entry.GrantedAccess] =
                        fileAccessCounts.GetValueOrDefault(entry.GrantedAccess) + 1;
                }
            }
        }

        var rows = new List<HandleTypeSnapshot>();
        ulong unmappedSystemHandles = 0;
        for (var typeIndex = 0; typeIndex < systemCounts.Length; typeIndex++)
        {
            var count = systemCounts[typeIndex];
            if (count == 0)
            {
                continue;
            }

            var nativeIndex = checked((ushort)typeIndex);
            var mapped = typeNames.TryGetValue(nativeIndex, out var typeName);
            if (!mapped)
            {
                typeName = $"TypeIndex #{nativeIndex}";
                unmappedSystemHandles += count;
            }

            rows.Add(new HandleTypeSnapshot(
                nativeIndex,
                typeName!,
                count,
                allCounts[typeIndex],
                systemTotal == 0
                    ? 0
                    : Math.Round(count * 100d / systemTotal, 2)));
        }

        var fileHandleCount = fileTypeIndex == 0
            ? 0
            : systemCounts[fileTypeIndex];
        var fileAccessPatterns = fileAccessCounts
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key)
            .Select(item => new FileHandleAccessSnapshot(
                item.Key,
                DecodeFileAccess(item.Key),
                item.Value,
                fileHandleCount == 0
                    ? 0
                    : Math.Round(item.Value * 100d / fileHandleCount, 2)))
            .ToArray();
        var fileHandleSamples = SampleFileHandles(
            entries,
            reportedCount,
            fileTypeIndex,
            fileHandleCount,
            cancellationToken);

        return new SystemHandleTypeProbeResult(
            rows
                .OrderByDescending(item => item.SystemHandleCount)
                .ThenBy(item => item.TypeName, StringComparer.Ordinal)
                .ToArray(),
            fileAccessPatterns,
            fileHandleSamples,
            systemTotal,
            reportedCount,
            unmappedSystemHandles,
            typeNames.Count,
            buffer.Length);
    }

    private static unsafe IReadOnlyList<SystemFileHandleDescriptor>
        SampleFileHandles(
            SystemHandleTableEntryInfoEx* entries,
            ulong reportedCount,
            ushort fileTypeIndex,
            ulong fileHandleCount,
            CancellationToken cancellationToken)
    {
        const int maximumSamples = 512;
        if (fileTypeIndex == 0 || fileHandleCount == 0)
        {
            return [];
        }

        var stride = Math.Max(
            1UL,
            fileHandleCount / maximumSamples);
        var nextOrdinal = stride / 2;
        ulong fileOrdinal = 0;
        var result = new List<SystemFileHandleDescriptor>(maximumSamples);
        for (ulong index = 0;
             index < reportedCount && result.Count < maximumSamples;
             index++)
        {
            if ((index & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var entry = entries[index];
            if (entry.UniqueProcessId.ToUInt64() != 4 ||
                entry.ObjectTypeIndex != fileTypeIndex)
            {
                continue;
            }

            if (fileOrdinal >= nextOrdinal)
            {
                result.Add(new SystemFileHandleDescriptor(
                    entry.HandleValue.ToUInt64(),
                    entry.GrantedAccess));
                nextOrdinal = checked(nextOrdinal + stride);
            }

            fileOrdinal++;
        }

        return result;
    }

    private static string DecodeFileAccess(uint access)
    {
        const uint fileGenericRead = 0x00120089;
        const uint fileReadData = 0x00000001;
        const uint fileWriteData = 0x00000002;
        const uint fileAppendData = 0x00000004;
        const uint fileReadEa = 0x00000008;
        const uint fileWriteEa = 0x00000010;
        const uint fileExecute = 0x00000020;
        const uint fileDeleteChild = 0x00000040;
        const uint fileReadAttributes = 0x00000080;
        const uint fileWriteAttributes = 0x00000100;
        const uint delete = 0x00010000;
        const uint readControl = 0x00020000;
        const uint writeDac = 0x00040000;
        const uint writeOwner = 0x00080000;
        const uint synchronize = 0x00100000;

        var rights = new List<string>();
        AddRight(access, fileReadData, "ReadData", rights);
        AddRight(access, fileWriteData, "WriteData", rights);
        AddRight(access, fileAppendData, "AppendData", rights);
        AddRight(access, fileReadEa, "ReadEA", rights);
        AddRight(access, fileWriteEa, "WriteEA", rights);
        AddRight(access, fileExecute, "Execute", rights);
        AddRight(access, fileDeleteChild, "DeleteChild", rights);
        AddRight(access, fileReadAttributes, "ReadAttributes", rights);
        AddRight(access, fileWriteAttributes, "WriteAttributes", rights);
        AddRight(access, delete, "Delete", rights);
        AddRight(access, readControl, "ReadControl", rights);
        AddRight(access, writeDac, "WriteDac", rights);
        AddRight(access, writeOwner, "WriteOwner", rights);
        AddRight(access, synchronize, "Synchronize", rights);
        var decoded = rights.Count == 0
            ? "NoneOrSpecial"
            : string.Join("+", rights);
        return access == fileGenericRead
            ? $"FileGenericRead ({decoded})"
            : decoded;
    }

    private static void AddRight(
        uint access,
        uint flag,
        string name,
        ICollection<string> rights)
    {
        if ((access & flag) != 0)
        {
            rights.Add(name);
        }
    }

    private static NativeBuffer QueryVariableBuffer(
        NativeBufferQuery query,
        int initialBytes,
        string operation)
    {
        uint requiredBytes;
        var probeStatus = query(IntPtr.Zero, 0, out requiredBytes);
        var requestedBytes = Math.Max(
            initialBytes,
            requiredBytes > 0
                ? checked((int)Math.Min(
                    MaximumBufferBytes,
                    (long)requiredBytes + 1024 * 1024))
                : initialBytes);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (requestedBytes <= 0 || requestedBytes > MaximumBufferBytes)
            {
                throw new InvalidDataException(
                    $"{operation} requested an unsafe buffer size: {requestedBytes:n0} bytes.");
            }

            var buffer = new NativeBuffer(requestedBytes);
            var status = query(
                buffer.Pointer,
                checked((uint)buffer.Length),
                out requiredBytes);
            if (status >= 0)
            {
                return buffer;
            }

            buffer.Dispose();
            if (!IsBufferResizeStatus(status))
            {
                throw new InvalidOperationException(
                    $"{operation} failed with NTSTATUS 0x{status:x8}.");
            }

            var grownBytes = Math.Max(
                (long)requestedBytes * 2,
                (long)requiredBytes + 1024 * 1024);
            if (grownBytes > MaximumBufferBytes)
            {
                throw new InvalidDataException(
                    $"{operation} requires {grownBytes:n0} bytes, above the {MaximumBufferBytes:n0}-byte safety limit.");
            }

            requestedBytes = checked((int)grownBytes);
        }

        throw new InvalidOperationException(
            $"{operation} did not stabilize after repeated buffer growth; initial NTSTATUS was 0x{probeStatus:x8}.");
    }

    private static bool IsBufferResizeStatus(int status)
    {
        return status is
            StatusInfoLengthMismatch or
            StatusBufferOverflow or
            StatusBufferTooSmall;
    }

    private static string ReadUnicodeString(
        UnicodeString value,
        long bufferStart,
        long bufferEnd)
    {
        if (value.Length == 0 || value.Buffer == IntPtr.Zero)
        {
            return "";
        }

        if ((value.Length & 1) != 0 || value.Length > 1024)
        {
            throw new InvalidDataException(
                $"Object type name has an invalid byte length: {value.Length}.");
        }

        var start = value.Buffer.ToInt64();
        var end = checked(start + value.Length);
        if (start < bufferStart || end > bufferEnd)
        {
            throw new InvalidDataException(
                "Object type name points outside the returned buffer.");
        }

        return Marshal.PtrToStringUni(
                   value.Buffer,
                   value.Length / sizeof(char)) ??
               "";
    }

    private static long AlignUp(long value, int alignment)
    {
        var mask = alignment - 1L;
        return checked((value + mask) & ~mask);
    }

    private static T LoadExport<T>(IntPtr nativeModule, string name)
        where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(nativeModule, name, out var address))
        {
            throw new EntryPointNotFoundException(
                $"{name} was not exported by ntdll.dll.");
        }

        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NtQuerySystemInformationDelegate(
        int informationClass,
        IntPtr information,
        uint informationLength,
        out uint returnLength);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int NtQueryObjectDelegate(
        IntPtr handle,
        int informationClass,
        IntPtr information,
        uint informationLength,
        out uint returnLength);

    private delegate int NativeBufferQuery(
        IntPtr buffer,
        uint length,
        out uint returnedLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GenericMapping
    {
        public uint GenericRead;
        public uint GenericWrite;
        public uint GenericExecute;
        public uint GenericAll;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectTypeInformation
    {
        public UnicodeString TypeName;
        public uint TotalNumberOfObjects;
        public uint TotalNumberOfHandles;
        public uint TotalPagedPoolUsage;
        public uint TotalNonPagedPoolUsage;
        public uint TotalNamePoolUsage;
        public uint TotalHandleTableUsage;
        public uint HighWaterNumberOfObjects;
        public uint HighWaterNumberOfHandles;
        public uint HighWaterPagedPoolUsage;
        public uint HighWaterNonPagedPoolUsage;
        public uint HighWaterNamePoolUsage;
        public uint HighWaterHandleTableUsage;
        public uint InvalidAttributes;
        public GenericMapping GenericMapping;
        public uint ValidAccessMask;
        public byte SecurityRequired;
        public byte MaintainHandleCount;
        public byte TypeIndex;
        public byte ReservedByte;
        public uint PoolType;
        public uint DefaultPagedPoolCharge;
        public uint DefaultNonPagedPoolCharge;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemHandleTableEntryInfoEx
    {
        public IntPtr Object;
        public UIntPtr UniqueProcessId;
        public UIntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    private sealed class NativeBuffer : IDisposable
    {
        public NativeBuffer(int length)
        {
            Length = length;
            Pointer = Marshal.AllocHGlobal(length);
        }

        public IntPtr Pointer { get; private set; }
        public int Length { get; }

        public void Dispose()
        {
            if (Pointer == IntPtr.Zero)
            {
                return;
            }

            Marshal.FreeHGlobal(Pointer);
            Pointer = IntPtr.Zero;
        }
    }
}
