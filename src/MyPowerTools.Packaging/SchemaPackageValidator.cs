using System.Text.Json;
using System.Collections.Concurrent;
using Json.Schema;

namespace MyPowerTools.Packaging;

public sealed class SchemaPackageValidator
{
    private static readonly ConcurrentDictionary<string, Lazy<JsonSchema>> SchemaCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _schemaDirectory;

    public SchemaPackageValidator(string schemaDirectory)
    {
        _schemaDirectory = schemaDirectory;
    }

    public PackageValidationReport ValidatePackageDirectory(string packageDirectory)
    {
        var issues = new List<ValidationIssue>();
        var reader = new PackageReader();

        try
        {
            var packagePath = Path.Combine(packageDirectory, "package.json");
            if (File.Exists(packagePath))
            {
                issues.AddRange(ValidateJson(packagePath, "package.schema.json"));
            }

            var definition = reader.ReadPackageDirectory(packageDirectory);
            if (!File.Exists(packagePath))
            {
                issues.Add(new ValidationIssue(packageDirectory, "info", "Single-module package synthesized from module.json."));
            }

            foreach (var module in definition.Modules)
            {
                issues.AddRange(ValidateJson(module.ManifestPath, "module.schema.json"));
                issues.AddRange(ValidateUiSurfaces(module));
                issues.AddRange(ValidateCommandIndexes(definition, module));
            }
        }
        catch (Exception ex)
        {
            issues.Add(new ValidationIssue(packageDirectory, "error", ex.Message));
        }

        return new PackageValidationReport(packageDirectory, issues);
    }

    public IReadOnlyList<PackageValidationReport> ValidatePackageRoot(string rootDirectory)
    {
        var reader = new PackageReader();
        return reader.DiscoverPackages(rootDirectory)
            .Select(package => ValidatePackageDirectory(package.Directory))
            .ToArray();
    }

    private IEnumerable<ValidationIssue> ValidateUiSurfaces(MptModuleDefinition module)
    {
        foreach (var relativePath in module.Manifest.UiSurfaces)
        {
            var surfacePath = Path.GetFullPath(Path.Combine(module.Directory, relativePath));
            foreach (var issue in ValidateJson(surfacePath, "ui-surface.schema.json"))
            {
                yield return issue;
            }
        }
    }

    private IReadOnlyList<ValidationIssue> ValidateCommandIndexes(MptPackageDefinition package, MptModuleDefinition module)
    {
        var issues = new List<ValidationIssue>();
        var commandsPath = ResolveCommandsIndexPath(package, module);
        if (commandsPath is null)
        {
            return issues;
        }

        if (!File.Exists(commandsPath))
        {
            issues.Add(new ValidationIssue(commandsPath, "error", "commands.index.json does not exist."));
            return issues;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(commandsPath));
        }
        catch (Exception ex)
        {
            issues.Add(new ValidationIssue(commandsPath, "error", ex.Message));
            return issues;
        }

        using (document)
        {
            var commands = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : document.RootElement.TryGetProperty("commands", out var commandsElement) && commandsElement.ValueKind == JsonValueKind.Array
                    ? commandsElement.EnumerateArray().ToArray()
                    : [];

            foreach (var command in commands)
            {
                var result = GetSchema(Path.Combine(_schemaDirectory, "command.schema.json")).Evaluate(command, new EvaluationOptions
                {
                    OutputFormat = OutputFormat.List
                });
                if (!result.IsValid)
                {
                    issues.Add(new ValidationIssue(commandsPath, "error", "Command entry failed command.schema.json validation."));
                }
            }
        }

        return issues;
    }

    private static string? ResolveCommandsIndexPath(MptPackageDefinition package, MptModuleDefinition module)
    {
        if (module.Manifest.StaticIndexes is not null &&
            module.Manifest.StaticIndexes.TryGetValue("commands", out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            var relative = element.GetString();
            return string.IsNullOrWhiteSpace(relative)
                ? null
                : Path.GetFullPath(Path.Combine(package.Directory, relative));
        }

        var defaultPath = Path.Combine(module.Directory, "commands.index.json");
        return File.Exists(defaultPath) ? defaultPath : null;
    }

    private IReadOnlyList<ValidationIssue> ValidateJson(string instancePath, string schemaFileName)
    {
        if (!File.Exists(instancePath))
        {
            return [new ValidationIssue(instancePath, "error", "Referenced file does not exist.")];
        }

        var schemaPath = Path.Combine(_schemaDirectory, schemaFileName);
        if (!File.Exists(schemaPath))
        {
            return [new ValidationIssue(schemaPath, "error", "Schema file does not exist.")];
        }

        try
        {
            var schema = GetSchema(schemaPath);
            var document = JsonDocument.Parse(File.ReadAllText(instancePath));

            var result = schema.Evaluate(document.RootElement, new EvaluationOptions
            {
                OutputFormat = OutputFormat.List
            });

            return result.IsValid
                ? []
                : [new ValidationIssue(instancePath, "error", $"Schema validation failed against {schemaFileName}.")];
        }
        catch (Exception ex)
        {
            return [new ValidationIssue(instancePath, "error", ex.Message)];
        }
    }

    private JsonSchema GetSchema(string schemaPath)
    {
        return SchemaCache.GetOrAdd(
            Path.GetFullPath(schemaPath),
            path => new Lazy<JsonSchema>(() => JsonSchema.FromText(File.ReadAllText(path)))).Value;
    }
}
