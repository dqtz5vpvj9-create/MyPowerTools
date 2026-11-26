using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace MyPowerTools.StandaloneHost;

/// <summary>
/// Resolves managed UI/SDK/tool assemblies and native SkiaSharp/HarfBuzzSharp libraries
/// across tool-local folders and shared MyPowerTools runtime locations, enabling 150 KB - 800 KB
/// composable tools without duplicating Avalonia or Skia in every folder.
/// </summary>
public static class SharedRuntimeAssemblyResolver
{
    private static readonly object Gate = new();
    private static readonly List<string> ProbeDirectories = [];
    private static bool _installed;

    public static void Install(string? toolRoot, IEnumerable<string>? extraProbeDirectories = null)
    {
        lock (Gate)
        {
            RebuildProbeDirectoriesLocked(toolRoot, extraProbeDirectories);
            if (!_installed)
            {
                AssemblyLoadContext.Default.Resolving += OnResolvingManagedAssembly;
                AssemblyLoadContext.Default.ResolvingUnmanagedDll += OnResolvingUnmanagedDll;
                _installed = true;
            }
        }
    }

    public static void RegisterToolRoot(string toolRoot)
    {
        if (string.IsNullOrWhiteSpace(toolRoot))
        {
            return;
        }

        lock (Gate)
        {
            var existing = ProbeDirectories.ToArray();
            RebuildProbeDirectoriesLocked(toolRoot, existing);
        }
    }

    public static void AddProbeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory));
        if (!Directory.Exists(full))
        {
            return;
        }

        lock (Gate)
        {
            if (!ProbeDirectories.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                ProbeDirectories.Insert(0, full);
            }
        }
    }

    public static IReadOnlyList<string> BuildOrderedProbeDirectories(
        string? toolRoot,
        string? appBaseDirectory = null,
        string? localAppDataDirectory = null,
        IEnumerable<string>? extraDirectories = null)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            try
            {
                var expanded = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate));
                if (seen.Add(expanded))
                {
                    ordered.Add(expanded);
                }
            }
            catch
            {
                // Ignore invalid path tokens.
            }
        }

        if (!string.IsNullOrWhiteSpace(toolRoot))
        {
            var fullToolRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(toolRoot));
            AddCandidate(fullToolRoot);
            AddCandidate(Path.Combine(fullToolRoot, "app"));
            AddCandidate(Path.Combine(fullToolRoot, "surface"));
            AddCandidate(Path.Combine(fullToolRoot, "module"));
        }

        var baseDir = string.IsNullOrWhiteSpace(appBaseDirectory)
            ? AppContext.BaseDirectory
            : appBaseDirectory;
        AddCandidate(baseDir);

        if (!string.IsNullOrWhiteSpace(toolRoot))
        {
            var fullToolRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(toolRoot));
            AddCandidate(Path.Combine(fullToolRoot, "..", "shared-ui"));
        }

        var envExtra = Environment.GetEnvironmentVariable(StandaloneCliOptions.SharedUiDirsEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envExtra))
        {
            foreach (var part in envExtra.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddCandidate(part);
            }
        }

        if (extraDirectories is not null)
        {
            foreach (var extra in extraDirectories)
            {
                AddCandidate(extra);
            }
        }

        var localAppData = string.IsNullOrWhiteSpace(localAppDataDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppDataDirectory;
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            AddCandidate(Path.Combine(localAppData, "MyPowerTools", "shared-ui"));
            AddCandidate(Path.Combine(localAppData, "Programs", "MyPowerTools", "Shell"));
            AddCandidate(Path.Combine(localAppData, "Programs", "MyPowerTools"));
        }

        return ordered;
    }

    public static string? TryResolveManagedAssemblyPath(AssemblyName assemblyName, IEnumerable<string> probeDirectories)
    {
        var simpleName = assemblyName.Name;
        if (string.IsNullOrWhiteSpace(simpleName) ||
            simpleName.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var dllFileName = simpleName + ".dll";
        foreach (var dir in probeDirectories)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            var candidate = Path.Combine(dir, dllFileName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    public static string? TryResolveNativeLibraryPath(
        string unmanagedDllName,
        IEnumerable<string> probeDirectories,
        string? ridOverride = null)
    {
        if (string.IsNullOrWhiteSpace(unmanagedDllName))
        {
            return null;
        }

        var rids = GetCandidateRuntimeIdentifiers(ridOverride);
        var fileNames = GetCandidateNativeFileNames(unmanagedDllName, ridOverride);

        foreach (var dir in probeDirectories)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var rid in rids)
            {
                var nativeDir = Path.Combine(dir, "runtimes", rid, "native");
                if (Directory.Exists(nativeDir))
                {
                    foreach (var fileName in fileNames)
                    {
                        var candidate = Path.Combine(nativeDir, fileName);
                        if (File.Exists(candidate))
                        {
                            return Path.GetFullPath(candidate);
                        }
                    }
                }
            }

            foreach (var fileName in fileNames)
            {
                var directCandidate = Path.Combine(dir, fileName);
                if (File.Exists(directCandidate))
                {
                    return Path.GetFullPath(directCandidate);
                }
            }
        }

        return null;
    }

    private static void RebuildProbeDirectoriesLocked(string? toolRoot, IEnumerable<string>? extraDirectories)
    {
        var updated = BuildOrderedProbeDirectories(toolRoot, extraDirectories: extraDirectories);
        ProbeDirectories.Clear();
        ProbeDirectories.AddRange(updated);
    }

    private static string[] SnapshotProbeDirectories()
    {
        lock (Gate)
        {
            return ProbeDirectories.ToArray();
        }
    }

    private static Assembly? OnResolvingManagedAssembly(AssemblyLoadContext context, AssemblyName name)
    {
        var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(asm => string.Equals(asm.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
        if (alreadyLoaded is not null)
        {
            return alreadyLoaded;
        }

        var resolvedPath = TryResolveManagedAssemblyPath(name, SnapshotProbeDirectories());
        return resolvedPath is null ? null : context.LoadFromAssemblyPath(resolvedPath);
    }

    private static IntPtr OnResolvingUnmanagedDll(Assembly requestingAssembly, string unmanagedDllName)
    {
        var probeList = new List<string>();
        var requestingDir = Path.GetDirectoryName(requestingAssembly.Location);
        if (!string.IsNullOrWhiteSpace(requestingDir))
        {
            probeList.Add(requestingDir);
        }

        probeList.AddRange(SnapshotProbeDirectories());

        var resolvedPath = TryResolveNativeLibraryPath(unmanagedDllName, probeList);
        if (resolvedPath is not null && NativeLibrary.TryLoad(resolvedPath, out var handle))
        {
            return handle;
        }

        return IntPtr.Zero;
    }

    private static IReadOnlyList<string> GetCandidateRuntimeIdentifiers(string? ridOverride)
    {
        var rids = new List<string>();
        if (!string.IsNullOrWhiteSpace(ridOverride))
        {
            rids.Add(ridOverride);
            return rids;
        }

        var currentRid = RuntimeInformation.RuntimeIdentifier;
        if (!string.IsNullOrWhiteSpace(currentRid))
        {
            rids.Add(currentRid);
        }

        var archSuffix = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64"
        };

        var portableRid = OperatingSystem.IsWindows()
            ? $"win-{archSuffix}"
            : OperatingSystem.IsMacOS()
                ? $"osx-{archSuffix}"
                : $"linux-{archSuffix}";

        if (!rids.Contains(portableRid, StringComparer.OrdinalIgnoreCase))
        {
            rids.Add(portableRid);
        }

        return rids;
    }

    private static IReadOnlyList<string> GetCandidateNativeFileNames(string unmanagedDllName, string? ridOverride)
    {
        var names = new List<string>();
        void AddName(string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !names.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(value);
            }
        }

        var cleanRaw = Path.GetFileName(unmanagedDllName);
        var stem = cleanRaw;
        foreach (var ext in new[] { ".dll", ".so", ".dylib" })
        {
            if (stem.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                stem = stem[..^ext.Length];
                break;
            }
        }

        var bareStem = stem.StartsWith("lib", StringComparison.OrdinalIgnoreCase) && stem.Length > 3
            ? stem[3..]
            : stem;

        var isWin = ridOverride is not null
            ? ridOverride.StartsWith("win", StringComparison.OrdinalIgnoreCase)
            : OperatingSystem.IsWindows();
        var isMac = ridOverride is not null
            ? ridOverride.StartsWith("osx", StringComparison.OrdinalIgnoreCase)
            : OperatingSystem.IsMacOS();

        if (isWin)
        {
            AddName($"lib{bareStem}.dll");
            AddName($"{bareStem}.dll");
            AddName($"{stem}.dll");
            AddName(cleanRaw);
        }
        else if (isMac)
        {
            AddName($"lib{bareStem}.dylib");
            AddName($"{bareStem}.dylib");
            AddName(cleanRaw);
        }
        else
        {
            AddName($"lib{bareStem}.so");
            AddName($"{bareStem}.so");
            AddName(cleanRaw);
        }

        return names;
    }
}
