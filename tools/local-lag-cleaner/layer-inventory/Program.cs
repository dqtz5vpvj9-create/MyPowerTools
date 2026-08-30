using System.Linq;

internal static class Program
{
    private static int Main(string[] args)
    {
        var root = args.Length > 0
            ? args[0]
            : @"C:\ProgramData\Microsoft\Windows\Containers\Layers";
        var outDir = args.Length > 1
            ? args[1]
            : Path.Combine(Path.GetTempPath(), "si-handle-export");
        Directory.CreateDirectory(outDir);

        var tsvPath = Path.Combine(outDir, "layer-files.tsv");
        var summaryPath = Path.Combine(outDir, "layer-classification.txt");

        var byLayer = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var byKind = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var byTop = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var byExt = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var byMarker = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["WinSxS"] = 0,
            ["System32"] = 0,
            ["SysWOW64"] = 0,
            ["INF"] = 0,
            ["WinSxS-Temp"] = 0,
            ["ProgramData"] = 0,
            ["Users"] = 0,
            ["EFI"] = 0,
            ["Microsoft.NET"] = 0,
        };
        long files = 0, dirs = 0, denied = 0, errors = 0, total = 0;

        using var tsv = new StreamWriter(tsvPath, false, new System.Text.UTF8Encoding(false));
        tsv.WriteLine("Kind\tLayer\tTop\tExt\tPath");

        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine("Missing " + root);
            return 2;
        }

        foreach (var layerDir in Directory.GetDirectories(root))
        {
            var layer = Path.GetFileName(layerDir);
            var filesRoot = Path.Combine(layerDir, "Files");
            if (!Directory.Exists(filesRoot))
                continue;

            var stack = new Stack<string>();
            stack.Push(filesRoot);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                string[] entries;
                try
                {
                    entries = Directory.GetFileSystemEntries(dir);
                }
                catch (UnauthorizedAccessException)
                {
                    denied++;
                    continue;
                }
                catch (Exception)
                {
                    errors++;
                    continue;
                }

                foreach (var entry in entries)
                {
                    bool isDir;
                    try { isDir = (File.GetAttributes(entry) & FileAttributes.Directory) != 0; }
                    catch (UnauthorizedAccessException) { denied++; continue; }
                    catch { errors++; continue; }

                    if (isDir)
                        stack.Push(entry);

                    var rel = entry.Length > filesRoot.Length + 1
                        ? entry[(filesRoot.Length + 1)..]
                        : string.Empty;
                    var top = rel.Length == 0 ? "(root)" : rel.Split(Path.DirectorySeparatorChar)[0];
                    var ext = isDir ? "(dir)" : Path.GetExtension(entry);
                    if (string.IsNullOrEmpty(ext))
                        ext = "(noext)";

                    tsv.Write(isDir ? "dir" : "file");
                    tsv.Write('\t');
                    tsv.Write(layer);
                    tsv.Write('\t');
                    tsv.Write(top);
                    tsv.Write('\t');
                    tsv.Write(ext);
                    tsv.Write('\t');
                    tsv.WriteLine(entry);

                    total++;
                    if (isDir) dirs++; else files++;
                    Add(byLayer, layer);
                    Add(byKind, isDir ? "dir" : "file");
                    Add(byTop, top);
                    Add(byExt, ext.ToLowerInvariant());
                    if (rel.Contains(@"WinSxS\Temp\", StringComparison.OrdinalIgnoreCase)) Add(byMarker, "WinSxS-Temp");
                    if (rel.Contains(@"\WinSxS\", StringComparison.OrdinalIgnoreCase) || rel.StartsWith(@"WinSxS\", StringComparison.OrdinalIgnoreCase)) Add(byMarker, "WinSxS");
                    if (rel.Contains(@"\System32\", StringComparison.OrdinalIgnoreCase) || rel.StartsWith(@"Windows\System32\", StringComparison.OrdinalIgnoreCase)) Add(byMarker, "System32");
                    if (rel.Contains(@"\SysWOW64\", StringComparison.OrdinalIgnoreCase)) Add(byMarker, "SysWOW64");
                    if (rel.Contains(@"\INF\", StringComparison.OrdinalIgnoreCase) || rel.StartsWith(@"Windows\INF\", StringComparison.OrdinalIgnoreCase)) Add(byMarker, "INF");
                    if (rel.Contains(@"ProgramData\", StringComparison.OrdinalIgnoreCase)) Add(byMarker, "ProgramData");
                    if (rel.StartsWith(@"Users\", StringComparison.OrdinalIgnoreCase)) Add(byMarker, "Users");
                    if (rel.StartsWith(@"EFI\", StringComparison.OrdinalIgnoreCase)) Add(byMarker, "EFI");
                    if (rel.Contains(@"Microsoft.NET\", StringComparison.OrdinalIgnoreCase)) Add(byMarker, "Microsoft.NET");
                }
            }
        }

        using var sum = new StreamWriter(summaryPath, false, new System.Text.UTF8Encoding(false));
        void Line(string s) { sum.WriteLine(s); Console.WriteLine(s); }
        Line($"TOTAL={total:N0} files={files:N0} dirs={dirs:N0} denied={denied:N0} errors={errors:N0}");
        Line($"TSV={tsvPath}");
        Dump(sum, "By layer", byLayer);
        Dump(sum, "By kind", byKind);
        Dump(sum, "By Files\\ first folder", byTop);
        Dump(sum, "By marker", byMarker);
        Dump(sum, "By extension (top 40)", byExt, 40);
        Console.WriteLine("SUMMARY=" + summaryPath);
        return 0;
    }

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
            var line = $"{kv.Value,10:N0}  {kv.Key}";
            sum.WriteLine(line);
            Console.WriteLine(line);
        }
    }
}
