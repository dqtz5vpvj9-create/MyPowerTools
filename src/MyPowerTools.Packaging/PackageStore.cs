namespace MyPowerTools.Packaging;

public sealed class PackageStore
{
    private readonly PackageReader _reader = new();
    private readonly SchemaPackageValidator _validator;
    private readonly PackageTrustVerifier _trust = new();

    public PackageStore(string storeRoot, string schemaDirectory)
    {
        StoreRoot = Path.GetFullPath(storeRoot);
        _validator = new SchemaPackageValidator(schemaDirectory);
        Directory.CreateDirectory(StoreRoot);
    }

    public string StoreRoot { get; }

    public PackageInstallResult Install(string packageDirectory)
    {
        var definition = _reader.ReadPackageDirectory(packageDirectory);
        var report = _validator.ValidatePackageDirectory(packageDirectory);
        if (!report.IsValid)
        {
            return new PackageInstallResult(false, definition.Package.Id, "", report.Issues);
        }

        var trustReport = _trust.Verify(packageDirectory, PackageTrustPolicy.LocalDevelopment);
        if (!trustReport.IsTrusted)
        {
            return new PackageInstallResult(false, definition.Package.Id, "", trustReport.Issues);
        }

        var target = ResolvePackageTarget(definition.Package.Id);
        var backup = target + ".rollback";
        if (Directory.Exists(backup))
        {
            Directory.Delete(backup, recursive: true);
        }

        if (Directory.Exists(target))
        {
            Directory.Move(target, backup);
        }

        try
        {
            CopyDirectory(packageDirectory, target);
            _trust.WriteLocalSignatureHook(target);
            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }

            return new PackageInstallResult(true, definition.Package.Id, target, []);
        }
        catch (Exception ex)
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            if (Directory.Exists(backup))
            {
                Directory.Move(backup, target);
            }

            return new PackageInstallResult(false, definition.Package.Id, target, [new ValidationIssue(target, "error", ex.Message)]);
        }
    }

    public PackageInstallResult Uninstall(string packageId)
    {
        var target = ResolvePackageTarget(packageId);
        if (!Directory.Exists(target))
        {
            return new PackageInstallResult(false, packageId, target, [new ValidationIssue(target, "error", "Package is not installed.")]);
        }

        var rollback = target + ".rollback";
        if (Directory.Exists(rollback))
        {
            Directory.Delete(rollback, recursive: true);
        }

        Directory.Move(target, rollback);
        return new PackageInstallResult(true, packageId, rollback, []);
    }

    public PackageInstallResult Rollback(string packageId)
    {
        var target = ResolvePackageTarget(packageId);
        var rollback = target + ".rollback";
        if (!Directory.Exists(rollback))
        {
            return new PackageInstallResult(false, packageId, target, [new ValidationIssue(rollback, "error", "Rollback package does not exist.")]);
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Directory.Move(rollback, target);
        return new PackageInstallResult(true, packageId, target, []);
    }

    public IReadOnlyList<ValidationIssue> Repair(string packageId)
    {
        var target = ResolvePackageTarget(packageId);
        if (!Directory.Exists(target))
        {
            return [new ValidationIssue(target, "error", "Package is not installed.")];
        }

        var issues = _trust.Verify(target, PackageTrustPolicy.LocalDevelopment).Issues;
        return issues.Count == 0 ? [] : issues;
    }

    private string ResolvePackageTarget(string packageId)
    {
        var target = Path.GetFullPath(Path.Combine(StoreRoot, packageId));
        var storeRoot = StoreRoot.EndsWith(Path.DirectorySeparatorChar)
            ? StoreRoot
            : StoreRoot + Path.DirectorySeparatorChar;
        if (!target.StartsWith(storeRoot, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(target, StoreRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Package target escapes store root.");
        }

        return target;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}

public sealed record PackageInstallResult(bool Success, string PackageId, string TargetPath, IReadOnlyList<ValidationIssue> Issues);
