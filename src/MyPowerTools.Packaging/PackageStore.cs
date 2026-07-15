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
            DeleteDirectoryWithRetry(backup);
        }

        if (Directory.Exists(target))
        {
            MoveDirectoryWithRetry(target, backup);
        }

        try
        {
            CopyDirectory(packageDirectory, target);
            _trust.WriteLocalSignatureHook(target);
            if (Directory.Exists(backup))
            {
                DeleteDirectoryWithRetry(backup);
            }

            return new PackageInstallResult(true, definition.Package.Id, target, []);
        }
        catch (Exception ex)
        {
            if (Directory.Exists(target))
            {
                DeleteDirectoryWithRetry(target);
            }

            if (Directory.Exists(backup))
            {
                MoveDirectoryWithRetry(backup, target);
            }

            return new PackageInstallResult(false, definition.Package.Id, target, [new ValidationIssue(target, "error", ex.Message)]);
        }
    }

    public PackageInstallResult Uninstall(
        string packageId,
        IEnumerable<string>? declaredDataRoots = null,
        bool purgeData = false)
    {
        var target = ResolvePackageTarget(packageId);
        if (!Directory.Exists(target))
        {
            return new PackageInstallResult(false, packageId, target, [new ValidationIssue(target, "error", "Package is not installed.")]);
        }

        var rollback = target + ".rollback";
        if (Directory.Exists(rollback))
        {
            DeleteDirectoryWithRetry(rollback);
        }

        MoveDirectoryWithRetry(target, rollback);
        var dataIssues = new ToolDataRetentionManager().ApplyUninstallPolicy(declaredDataRoots ?? [], purgeData);
        return new PackageInstallResult(dataIssues.Count == 0, packageId, rollback, dataIssues);
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
            DeleteDirectoryWithRetry(target);
        }

        MoveDirectoryWithRetry(rollback, target);
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

    private static void MoveDirectoryWithRetry(string source, string target)
    {
        RunFileSystemOperationWithRetry(() => Directory.Move(source, target), $"move directory '{source}' to '{target}'");
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        RunFileSystemOperationWithRetry(() => Directory.Delete(path, recursive: true), $"delete directory '{path}'");
    }

    private static void RunFileSystemOperationWithRetry(Action operation, string description)
    {
        const int maxAttempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception ex) when (IsTransientFileSystemException(ex) && attempt < maxAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50 * attempt));
            }
            catch (Exception ex) when (IsTransientFileSystemException(ex))
            {
                throw new IOException($"Could not {description} after {maxAttempts} attempts.", ex);
            }
        }
    }

    private static bool IsTransientFileSystemException(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException;
    }
}

public sealed record PackageInstallResult(bool Success, string PackageId, string TargetPath, IReadOnlyList<ValidationIssue> Issues);

/// <summary>
/// Applies the explicit data-retention decision made during tool uninstall. A normal uninstall
/// never touches tool data. Purge accepts only absolute, non-root directories and reports every
/// rejected or failed path as a validation issue.
/// </summary>
public sealed class ToolDataRetentionManager
{
    public IReadOnlyList<ValidationIssue> ApplyUninstallPolicy(IEnumerable<string> declaredDataRoots, bool purgeRequested)
    {
        if (!purgeRequested)
        {
            return [];
        }

        var issues = new List<ValidationIssue>();
        foreach (var declaredRoot in declaredDataRoots
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var expanded = Environment.ExpandEnvironmentVariables(declaredRoot.Trim());
            if (!Path.IsPathFullyQualified(expanded))
            {
                issues.Add(new ValidationIssue(declaredRoot, "error", "A data root must be an absolute path before it can be purged."));
                continue;
            }

            var path = Path.GetFullPath(expanded)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var volumeRoot = Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "";
            if (string.Equals(path, volumeRoot, StringComparison.OrdinalIgnoreCase) || IsProtectedProfileRoot(path))
            {
                issues.Add(new ValidationIssue(path, "error", "Refusing to purge a filesystem or user-profile root."));
                continue;
            }

            if (!Directory.Exists(path))
            {
                continue;
            }

            try
            {
                DeleteDirectoryWithRetry(path);
            }
            catch (Exception ex)
            {
                issues.Add(new ValidationIssue(path, "error", ex.Message));
            }
        }

        return issues;
    }

    private static bool IsProtectedProfileRoot(string path)
    {
        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        };
        return protectedRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Any(root => string.Equals(path, root, StringComparison.OrdinalIgnoreCase));
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        const int attempts = 6;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < attempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }
    }
}
