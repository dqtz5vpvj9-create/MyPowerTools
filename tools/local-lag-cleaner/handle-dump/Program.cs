using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    private const int SystemExtendedHandleInformation = 64;
    private const int ObjectTypesInformation = 3;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(IntPtr h, int cls, IntPtr buf, int len, out int ret);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr token, bool disableAll, ref TokenPrivileges newState,
        int bufferLength, IntPtr prevState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public int PrivilegeCount;
        public uint LuidLow;
        public int LuidHigh;
        public int Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleEntry
    {
        public IntPtr Object;
        public IntPtr UniqueProcessId;
        public IntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
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
        public uint GenericRead;
        public uint GenericWrite;
        public uint GenericExecute;
        public uint GenericAll;
        public uint ValidAccessMask;
        public byte SecurityRequired;
        public byte MaintainHandleCount;
        public byte TypeIndex;
        public byte ReservedByte;
        public uint PoolType;
        public uint DefaultPagedPoolCharge;
        public uint DefaultNonPagedPoolCharge;
    }

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("analyze", StringComparison.OrdinalIgnoreCase))
            return Analyze(args.Skip(1).ToArray());

        EnableDebugPrivilege();
        int targetPid = args.Length > 0 ? int.Parse(args[0]) : 4;
        string outDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "si-handle-export");
        Directory.CreateDirectory(outDir);

        var types = LoadTypeMap();
        Console.WriteLine($"TYPE_COUNT={types.Count}");

        int len = 256 * 1024 * 1024;
        IntPtr buf = IntPtr.Zero;
        int st, ret;
        try
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                buf = Marshal.AllocHGlobal(len);
                st = NtQuerySystemInformation(SystemExtendedHandleInformation, buf, len, out ret);
                if (st == 0)
                    break;
                Marshal.FreeHGlobal(buf);
                buf = IntPtr.Zero;
                if (st != unchecked((int)0xC0000004)) // STATUS_INFO_LENGTH_MISMATCH
                {
                    Console.Error.WriteLine($"NtQuerySystemInformation failed 0x{st:X8}");
                    return 1;
                }
                len = Math.Max(len * 2, ret + 16 * 1024 * 1024);
            }

            if (buf == IntPtr.Zero)
            {
                Console.Error.WriteLine("Failed to allocate handle table buffer.");
                return 1;
            }

            long n = Marshal.ReadIntPtr(buf).ToInt64();
            Console.WriteLine($"SYSTEM_HANDLES={n:N0}");
            int size = Marshal.SizeOf<HandleEntry>();
            IntPtr p = IntPtr.Add(buf, IntPtr.Size * 2);

            var tsvPath = Path.Combine(outDir, $"pid{targetPid}-handles.tsv");
            var sumPath = Path.Combine(outDir, $"pid{targetPid}-classification.txt");
            var byType = new Dictionary<string, long>(StringComparer.Ordinal);
            var byAccess = new Dictionary<string, long>(StringComparer.Ordinal);
            var byAttr = new Dictionary<string, long>(StringComparer.Ordinal);
            var byTypeAccess = new Dictionary<string, long>(StringComparer.Ordinal);
            var objects = new HashSet<long>();
            var fileObjects = new HashSet<long>();
            var leakObjects = new HashSet<long>();
            long pidRows = 0, protectClose = 0, inherit = 0, objectZero = 0, fileRows = 0, leakRows = 0;

            using (var tsv = new StreamWriter(tsvPath, false, new UTF8Encoding(false), 1 << 20))
            {
                tsv.WriteLine("Handle\tType\tTypeIndex\tAccess\tAttributes\tProtectClose\tInherit\tObject");
                unsafe
                {
                    byte* basePtr = (byte*)p.ToPointer();
                    for (long i = 0; i < n; i++)
                    {
                        var e = *(HandleEntry*)(basePtr + i * size);
                        if (e.UniqueProcessId.ToInt64() != targetPid)
                            continue;
                        pidRows++;
                        if ((pidRows & 0x3FFFF) == 0)
                            Console.WriteLine($"  wrote {pidRows:N0} rows...");
                        types.TryGetValue(e.ObjectTypeIndex, out var typeName);
                        typeName ??= $"#{e.ObjectTypeIndex}";
                        bool prot = (e.HandleAttributes & 0x1) != 0;
                        bool inh = (e.HandleAttributes & 0x2) != 0;
                        if (prot) protectClose++;
                        if (inh) inherit++;
                        long obj = e.Object.ToInt64();
                        if (obj == 0) objectZero++;
                        else objects.Add(obj);
                        if (typeName == "File")
                        {
                            fileRows++;
                            if (obj != 0) fileObjects.Add(obj);
                            if (e.GrantedAccess == 0x00120089)
                            {
                                leakRows++;
                                if (obj != 0) leakObjects.Add(obj);
                            }
                        }

                        Add(byType, typeName);
                        var acc = $"0x{e.GrantedAccess:X8}";
                        Add(byAccess, acc);
                        Add(byTypeAccess, typeName + " " + acc);
                        Add(byAttr, $"prot={prot} inherit={inh}");

                        tsv.Write(e.HandleValue.ToInt64().ToString("X"));
                        tsv.Write('\t');
                        tsv.Write(typeName);
                        tsv.Write('\t');
                        tsv.Write(e.ObjectTypeIndex);
                        tsv.Write('\t');
                        tsv.Write(acc);
                        tsv.Write('\t');
                        tsv.Write("0x");
                        tsv.Write(e.HandleAttributes.ToString("X"));
                        tsv.Write('\t');
                        tsv.Write(prot ? '1' : '0');
                        tsv.Write('\t');
                        tsv.Write(inh ? '1' : '0');
                        tsv.Write('\t');
                        tsv.WriteLine(e.Object.ToInt64().ToString("X"));
                    }
                }
            }

            using var sum = new StreamWriter(sumPath, false, new UTF8Encoding(false));
            void Line(string s) { sum.WriteLine(s); Console.WriteLine(s); }
            Line($"PID={targetPid} ROWS={pidRows:N0} UNIQUE_OBJECTS={objects.Count:N0} OBJECT_PTR_ZERO={objectZero:N0} PROTECT_CLOSE={protectClose:N0} INHERIT={inherit:N0}");
            Line($"FILE_ROWS={fileRows:N0} FILE_UNIQUE_OBJECTS={fileObjects.Count:N0} LEAK_FILE_00120089={leakRows:N0} LEAK_UNIQUE_OBJECTS={leakObjects.Count:N0}");
            Line($"TSV={tsvPath}");
            Dump(sum, "By type", byType);
            Dump(sum, "By access mask (top 30)", byAccess, 30);
            Dump(sum, "By type+access (top 40)", byTypeAccess, 40);
            Dump(sum, "By attributes", byAttr);
            Console.WriteLine("SUMMARY=" + sumPath);
            return 0;
        }
        finally
        {
            if (buf != IntPtr.Zero)
                Marshal.FreeHGlobal(buf);
        }
    }

    private static void EnableDebugPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out var token))
        {
            Console.WriteLine("DEBUG_PRIV=OpenProcessToken failed " + Marshal.GetLastWin32Error());
            return;
        }
        try
        {
            if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid))
            {
                Console.WriteLine("DEBUG_PRIV=LookupPrivilegeValue failed " + Marshal.GetLastWin32Error());
                return;
            }
            var tp = new TokenPrivileges
            {
                PrivilegeCount = 1,
                LuidLow = (uint)(luid & 0xFFFFFFFF),
                LuidHigh = (int)(luid >> 32),
                Attributes = 0x00000002
            };
            if (!AdjustTokenPrivileges(token, false, ref tp, Marshal.SizeOf<TokenPrivileges>(), IntPtr.Zero, IntPtr.Zero))
            {
                Console.WriteLine("DEBUG_PRIV=AdjustTokenPrivileges failed " + Marshal.GetLastWin32Error());
                return;
            }
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine(err == 1300 ? "DEBUG_PRIV=not assigned" : "DEBUG_PRIV=enabled");
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static int Analyze(string[] args)
    {
        string pid4 = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "si-handle-export", "pid4-handles.tsv");
        string containers = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "si-handle-export", "system-containers-handles.tsv");
        string outDir = Path.GetDirectoryName(pid4) ?? Path.GetTempPath();
        var named = new HashSet<long>();
        foreach (var line in File.ReadLines(containers).Skip(1))
        {
            var parts = line.Split('\t');
            if (parts.Length < 7) continue;
            if (!parts[1].Equals("4", StringComparison.Ordinal)) continue;
            var h = parts[6];
            if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
            if (long.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var hv))
                named.Add(hv);
        }
        Console.WriteLine($"NAMED_CONTAINER_HANDLES={named.Count:N0}");

        long fileRows = 0, leakRows = 0, leakInNamed = 0, leakNotNamed = 0, fileNotLeak = 0;
        long prevLeak = -1, runs = 0, runLen = 0, maxRun = 0, inNamedRun = 0, notNamedRun = 0;
        long unnamedStreak = 0, maxUnnamedStreak = 0, switchCount = 0;
        long maxStreakStart = 0, maxStreakEnd = 0, streakStart = 0;
        bool? lastNamed = null;
        var samples = new List<string>();
        var window = new int[17];
        long windows = 0, windowNamedSum = 0;
        int w = 0, wNamed = 0;

        using var sampleWriter = new StreamWriter(Path.Combine(outDir, "pid4-unnamed-file-samples.txt"), false, new UTF8Encoding(false));
        sampleWriter.WriteLine("Handle\tAccess\tInContainersExport");

        foreach (var line in File.ReadLines(pid4).Skip(1))
        {
            var parts = line.Split('\t');
            if (parts.Length < 4 || parts[1] != "File") continue;
            fileRows++;
            if (!long.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var hv))
                continue;
            bool leak = parts[3].Equals("0x00120089", StringComparison.OrdinalIgnoreCase);
            if (!leak)
            {
                fileNotLeak++;
                continue;
            }
            leakRows++;
            bool isNamed = named.Contains(hv);
            if (isNamed) leakInNamed++;
            else leakNotNamed++;

            if (prevLeak >= 0 && hv == prevLeak + 4) { runLen++; }
            else
            {
                if (runLen > maxRun) maxRun = runLen;
                runs++;
                runLen = 1;
            }
            prevLeak = hv;
            if (isNamed) inNamedRun++; else notNamedRun++;

            if (lastNamed != null && lastNamed.Value != isNamed) switchCount++;
            lastNamed = isNamed;
            if (!isNamed)
            {
                if (unnamedStreak == 0) streakStart = hv;
                unnamedStreak++;
                if (unnamedStreak > maxUnnamedStreak)
                {
                    maxUnnamedStreak = unnamedStreak;
                    maxStreakStart = streakStart;
                    maxStreakEnd = hv;
                }
                if (samples.Count < 12 && unnamedStreak == 1)
                {
                    samples.Add($"0x{hv:X}");
                    sampleWriter.WriteLine($"{parts[0]}\t{parts[3]}\t0\tinterleaved");
                }
            }
            else unnamedStreak = 0;

            w++;
            if (isNamed) wNamed++;
            if (w == 16)
            {
                windows++;
                windowNamedSum += wNamed;
                window[wNamed]++;
                w = 0;
                wNamed = 0;
            }
        }
        if (runLen > maxRun) maxRun = runLen;

        var sumPath = Path.Combine(outDir, "pid4-file-composition.txt");
        using var sum = new StreamWriter(sumPath, false, new UTF8Encoding(false));
        void Line(string s) { sum.WriteLine(s); Console.WriteLine(s); }
        Line($"FILE_ROWS={fileRows:N0} FILE_NOT_LEAK_MASK={fileNotLeak:N0}");
        Line($"LEAK_FILE_00120089={leakRows:N0}");
        Line($"LEAK_HANDLE_STILL_IN_CONTAINERS_EXPORT={leakInNamed:N0}");
        Line($"LEAK_HANDLE_NOT_IN_CONTAINERS_EXPORT={leakNotNamed:N0}");
        Line($"NAMED_EXPORT_MINUS_STILL_PRESENT={named.Count - leakInNamed:N0}");
        Line($"CONSECUTIVE_HANDLE_RUNS={runs:N0} MAX_RUN_LEN={maxRun:N0} (step=4)");
        Line($"NAMED_UNNAMED_SWITCHES_IN_LEAK_STREAM={switchCount:N0}");
        Line($"MAX_UNNAMED_STREAK={maxUnnamedStreak:N0} START=0x{maxStreakStart:X} END=0x{maxStreakEnd:X}");
        if (windows > 0)
            Line($"PER_16_LEAK_HANDLES_AVG_NAMED={windowNamedSum / (double)windows:F3}");
        Line("PER_16_NAMED_COUNT_HISTOGRAM:");
        for (int i = 0; i < window.Length; i++)
        {
            if (window[i] == 0) continue;
            Line($"  named={i} windows={window[i]:N0}");
        }
        Line("SAMPLE_UNNAMED_HANDLES=" + string.Join(",", samples));
        Line("SUMMARY=" + sumPath);
        Line("SAMPLES=" + Path.Combine(outDir, "pid4-unnamed-file-samples.txt"));
        return 0;
    }

    private static Dictionary<ushort, string> LoadTypeMap()
    {
        var map = new Dictionary<ushort, string>();
        int len = 512 * 1024, ret;
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            int st = NtQueryObject(IntPtr.Zero, ObjectTypesInformation, buf, len, out ret);
            if (st != 0)
                return map;
            uint count = (uint)Marshal.ReadInt32(buf);
            int size = Marshal.SizeOf<ObjectTypeInformation>();
            long current = AlignUp(buf.ToInt64() + sizeof(uint), IntPtr.Size);
            long bufEnd = buf.ToInt64() + len;
            for (uint i = 0; i < count; i++)
            {
                var p = new IntPtr(current);
                var ti = Marshal.PtrToStructure<ObjectTypeInformation>(p);
                string name = ti.TypeName.Buffer != IntPtr.Zero && ti.TypeName.Length > 0
                    ? Marshal.PtrToStringUni(ti.TypeName.Buffer, ti.TypeName.Length / 2) ?? ""
                    : "";
                if (ti.TypeIndex > 0 && name.Length > 0)
                    map[ti.TypeIndex] = name;
                long structureEnd = current + size;
                long stringEnd = ti.TypeName.Buffer == IntPtr.Zero
                    ? structureEnd
                    : ti.TypeName.Buffer.ToInt64() + ti.TypeName.MaximumLength;
                current = AlignUp(Math.Max(structureEnd, stringEnd), IntPtr.Size);
                if (current > bufEnd)
                    break;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return map;
    }

    private static long AlignUp(long value, int alignment) =>
        (value + alignment - 1) & ~((long)alignment - 1);

    private static void Add(Dictionary<string, long> map, string key)
    {
        map.TryGetValue(key, out var n);
        map[key] = n + 1;
    }

    private static void Dump(StreamWriter sum, string title, Dictionary<string, long> map, int take = int.MaxValue)
    {
        sum.WriteLine();
        sum.WriteLine("==== " + title + " ====");
        Console.WriteLine();
        Console.WriteLine("==== " + title + " ====");
        foreach (var kv in map.OrderByDescending(x => x.Value).Take(take))
        {
            var line = $"{kv.Value,12:N0}  {kv.Key}";
            sum.WriteLine(line);
            Console.WriteLine(line);
        }
    }
}
