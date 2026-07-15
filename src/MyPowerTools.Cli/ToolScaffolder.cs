using System.Diagnostics;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using MyPowerTools.Packaging;

namespace MyPowerTools.Cli;

internal static class ToolScaffolder
{
    private static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        "web", "dotnet", "native", "headless"
    };

    public static int Create(string type, string id, string output, string sdkFeed)
    {
        if (!Types.Contains(type) || !IsId(id) || string.IsNullOrWhiteSpace(output))
        {
            Console.Error.WriteLine("create tool requires --type web|dotnet|native|headless, --id <id>, and --output <dir>.");
            return 2;
        }

        var directory = Path.GetFullPath(output);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Console.Error.WriteLine($"Output directory is not empty: {directory}");
            return 1;
        }

        Directory.CreateDirectory(directory);
        foreach (var file in Files(type, id, sdkFeed))
        {
            var relativePath = file.Key
                .Replace("__ID__", id, StringComparison.Ordinal)
                .Replace("__CLASS__", ClassName(id), StringComparison.Ordinal)
                .Replace('/', Path.DirectorySeparatorChar);
            var path = Path.Combine(directory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Value.Replace("__ID__", id).Replace("__CLASS__", ClassName(id)), new UTF8Encoding(false));
        }

        Console.WriteLine(directory);
        Console.WriteLine($"Created {type} tool '{id}'. Development mode allows dirty files and arbitrary branches.");
        return 0;
    }

    public static int Validate(string directory, string schemaDirectory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            Console.Error.WriteLine($"Tool directory was not found: {directory}");
            return 2;
        }

        var toolPath = Path.Combine(Path.GetFullPath(directory), "tool.json");
        if (!File.Exists(toolPath))
        {
            Console.Error.WriteLine($"tool.json was not found: {toolPath}");
            return 1;
        }

        try
        {
            var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(schemaDirectory, "tool.schema.json")));
            using var document = JsonDocument.Parse(File.ReadAllText(toolPath));
            var result = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (!result.IsValid)
            {
                foreach (var detail in result.Details?.Where(detail => !detail.IsValid) ?? Enumerable.Empty<EvaluationResults>())
                {
                    var errors = detail.Errors is null ? "schema validation failed" : string.Join("; ", detail.Errors.Values);
                    Console.Error.WriteLine($"error: {detail.InstanceLocation}: {errors}");
                }
                return 1;
            }

            var definition = new PackageReader().ReadDevelopmentToolDirectory(directory);
            var manifest = new PackageReader().ReadJson<MptToolManifest>(toolPath);
            foreach (var route in manifest.Routes)
            {
                var surface = route.Surface;
                if (surface is null)
                {
                    Console.Error.WriteLine($"error: route '{route.RouteId}' requires a surface object.");
                    return 1;
                }
                ValidateRelativeFile(directory, surface.StaticRoot, "staticRoot");
                ValidateRelativeFile(directory, surface.Assembly, "assembly", allowMissingBuildOutput: true);
            }
            ValidateRelativeFile(directory, manifest.Settings?.Schema ?? "", "settings.schema");
            Console.WriteLine($"Tool validation passed: {definition.Package.Id} ({manifest.Type}).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    public static int Pack(string directory, string? output, string schemaDirectory)
    {
        if (Validate(directory, schemaDirectory) != 0)
        {
            return 1;
        }

        directory = Path.GetFullPath(directory);
        var manifest = new PackageReader().ReadJson<MptToolManifest>(Path.Combine(directory, "tool.json"));
        output = string.IsNullOrWhiteSpace(output)
            ? Path.Combine(Path.GetDirectoryName(directory)!, $"{manifest.ToolId}.mptpkg")
            : Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var ignorePatterns = LoadIgnorePatterns(directory);
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => !IsIgnored(directory, path, ignorePatterns))
            .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.Ordinal)
            .ToArray();
        var source = SourceMetadata(directory, files);
        using (var stream = File.Create(output))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var file in files)
            {
                archive.CreateEntryFromFile(file, Path.GetRelativePath(directory, file).Replace('\\', '/'), CompressionLevel.Optimal);
            }
            var entry = archive.CreateEntry("source-manifest.json", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(JsonSerializer.Serialize(source, new JsonSerializerOptions { WriteIndented = true }));
        }

        Console.WriteLine(output);
        return 0;
    }

    private static IReadOnlyDictionary<string, string> Files(string type, string id, string sdkFeed)
    {
        var toolType = type + (type is "web" or "dotnet" ? "-surface" : "-tool");
        var surface = type switch
        {
            "web" => """{ "kind": "web", "source": "http://127.0.0.1:43110/", "staticRoot": "web", "openExternal": true, "allowedOrigins": ["http://127.0.0.1:43110"] }""",
            "dotnet" => """{ "kind": "dotnet", "assembly": "bin/Debug/net10.0/__CLASS__.dll", "type": "__CLASS__.ToolSurfaceFactory" }""",
            "native" => """{ "kind": "native", "source": "runtime.ps1" }""",
            _ => """{ "kind": "headless" }"""
        };
        var runtime = type switch
        {
            "web" => """{ "transport": "loopback-http", "endpoint": "http://127.0.0.1:43110", "command": "python", "args": ["serve.py"], "healthPath": "/api/status", "logsPath": "/api/logs", "timeoutMs": 5000 }""",
            "native" => """{ "transport": "stdio-jsonrpc", "command": "powershell", "args": ["-NoProfile", "-File", "runtime.ps1"] }""",
            "headless" => """{ "transport": "stdio-jsonrpc", "command": "python", "args": ["runtime.py"] }""",
            _ => """{ "transport": "none" }"""
        };
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tool.json"] = $$"""
            {
              "schemaVersion": "1.0",
              "version": "0.1.0",
              "toolId": "__ID__",
              "ownerModuleId": "__ID__",
              "title": "__ID__",
              "description": "A MyPowerTools {{toolType}} created by the SDK CLI.",
              "icon": "tool.external",
              "category": "External tools",
              "type": "{{toolType}}",
              "availability": "available",
              "primaryRouteId": "main",
              "routes": [
                { "routeId": "main", "surfaceId": "__ID__.main", "title": "Overview", "surface": {{surface}} }
              ],
              "homeCard": { "summary": "Open __ID__", "primaryActionLabel": "Open", "order": 500 },
              "runtime": {{runtime}},
              "settings": { "schema": "settings.schema.json", "values": "settings.json", "secrets": ["apiSecret"] },
              "commands": [
                { "id": "__ID__.health", "title": "Check health", "description": "Probe the configured runtime.", "method": "GET", "path": "/api/status" },
                { "id": "__ID__.refresh", "title": "Refresh", "description": "Refresh tool state.", "method": "POST", "path": "/api/refresh" },
                { "id": "__ID__.logs", "title": "Tail logs", "description": "Read recent runtime logs.", "method": "GET", "path": "/api/logs" }
              ],
              "permissions": [],
              "development": { "loose": true, "autoRefresh": false }
            }
            """,
            ["settings.schema.json"] = """
            { "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object", "properties": { "endpoint": { "type": "string" }, "connectionTimeoutMs": { "type": "integer", "minimum": 100 }, "autoRefresh": { "type": "boolean" }, "apiSecret": { "type": "string", "x-mpt-secret": true } } }
            """,
            ["settings.json"] = """{ "connectionTimeoutMs": 5000, "autoRefresh": true }""",
            ["README.md"] = """
            # __ID__

            Validate with `mypowertools validate tool .`, add this parent directory to
            `%LOCALAPPDATA%/MyPowerTools/settings/tool-directories.json`, then click
            **Refresh tools**. Build artifacts may be dirty and can come from any branch.
            Publish with `mypowertools pack tool .`.
            """
        };

        if (type == "web")
        {
            files["web/index.html"] = """<!doctype html><meta charset="utf-8"><title>__ID__</title><style>body{font:16px system-ui;margin:40px;background:#f6f8fa;color:#17202a}main{max-width:760px;background:white;border:1px solid #d8dee4;border-radius:12px;padding:32px}button{padding:10px 16px}</style><main><h1>__ID__</h1><p>This real page is hosted outside the MyPowerTools repository.</p><button onclick="location.reload()">Refresh</button></main>""";
            files["serve.py"] = WebServer;
        }
        else if (type == "dotnet")
        {
            files["NuGet.config"] = $$"""<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear/><add key="mpt-local" value="{{Path.GetFullPath(sdkFeed)}}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>""";
            files["__CLASS__.csproj"] = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><PackageReference Include="MyPowerTools.AvaloniaSdk" Version="0.2.0"/><PackageReference Include="MyPowerTools.ToolSdk" Version="0.2.0"/></ItemGroup></Project>""";
            files["ToolSurfaceFactory.cs"] = """using Avalonia.Controls; using MyPowerTools.AvaloniaSdk; namespace __CLASS__; public sealed class ToolSurfaceFactory : IMptAvaloniaSurfaceFactory { public Control CreateSurface(MptAvaloniaSurfaceContext context) => new Border { Padding = new Avalonia.Thickness(24), Child = new TextBlock { Text = "__ID__ loaded through MyPowerTools.AvaloniaSdk" } }; }""";
        }
        else if (type == "native")
        {
            files["runtime.ps1"] = """$ErrorActionPreference='Stop'; while(($line=[Console]::In.ReadLine()) -ne $null){ $request=$line|ConvertFrom-Json; @{jsonrpc='2.0';id=$request.id;result=@{state='ready';message='__ID__ native runtime ready'}}|ConvertTo-Json -Compress -Depth 5 }""";
        }
        else
        {
            files["runtime.py"] = """import json,sys,time\nseq=0\nfor line in sys.stdin:\n request=json.loads(line); seq+=1\n print(json.dumps({'jsonrpc':'2.0','id':request.get('id'),'result':{'state':'ready','eventSeq':seq,'time':time.time()}}),flush=True)\n""";
        }
        return files;
    }

    private const string WebServer = """
from http.server import ThreadingHTTPServer, SimpleHTTPRequestHandler
import json, os
class Handler(SimpleHTTPRequestHandler):
    def translate_path(self, path):
        raw = super().translate_path(path)
        return os.path.join(os.path.dirname(__file__), 'web', os.path.relpath(raw, os.getcwd()))
    def do_GET(self):
        if self.path == '/api/status':
            body=json.dumps({'state':'ready','summary':'__ID__ is running'}).encode(); self.send_response(200); self.send_header('Content-Type','application/json'); self.send_header('Content-Length',str(len(body))); self.end_headers(); self.wfile.write(body); return
        if self.path == '/api/logs':
            body=json.dumps({'lines':['__ID__ ready']}).encode(); self.send_response(200); self.send_header('Content-Type','application/json'); self.send_header('Content-Length',str(len(body))); self.end_headers(); self.wfile.write(body); return
        return super().do_GET()
ThreadingHTTPServer(('127.0.0.1',43110),Handler).serve_forever()
""";

    private static bool IsId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(character => char.IsLower(character) || char.IsDigit(character) || character is '.' or '-');

    private static string ClassName(string id) => string.Concat(id.Split(['.', '-'], StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static void ValidateRelativeFile(string directory, string value, string label, bool allowMissingBuildOutput = false)
    {
        if (string.IsNullOrWhiteSpace(value) || Uri.TryCreate(value, UriKind.Absolute, out _)) return;
        var path = Path.GetFullPath(Path.Combine(directory, value));
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{label} escapes the tool directory: {value}");
        if (!allowMissingBuildOutput && !File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException($"{label} was not found", path);
    }

    private static bool IsIgnored(string root, string path, IReadOnlyList<string> ignorePatterns)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative.Split('/').Any(part => part is ".git" or "obj" or "node_modules" or "__pycache__") ||
               relative.EndsWith(".mptpkg", StringComparison.OrdinalIgnoreCase) ||
               ignorePatterns.Any(pattern =>
                   pattern.EndsWith('/')
                       ? relative.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)
                       : FileSystemName.MatchesSimpleExpression(pattern, relative, ignoreCase: true));
    }

    private static IReadOnlyList<string> LoadIgnorePatterns(string root)
    {
        var path = Path.Combine(root, ".mptignore");
        if (!File.Exists(path))
        {
            return [];
        }
        return File.ReadAllLines(path)
            .Select(line => line.Trim().Replace('\\', '/'))
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
    }

    private static object SourceMetadata(string directory, IReadOnlyList<string> files)
    {
        var commit = Git(directory, "rev-parse", "HEAD");
        var status = Git(directory, "status", "--porcelain");
        return new
        {
            format = "mpt-source-v1",
            createdAt = DateTimeOffset.UtcNow,
            sourceDirectory = directory,
            commit = commit.Trim(),
            dirty = !string.IsNullOrWhiteSpace(status),
            files = files.Select(path => new { path = Path.GetRelativePath(directory, path).Replace('\\', '/'), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() })
        };
    }

    private static string Git(string directory, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            if (process is null) return "";
            var output = process.StandardOutput.ReadToEnd(); process.WaitForExit(3000);
            return process.ExitCode == 0 ? output : "";
        }
        catch { return ""; }
    }
}
