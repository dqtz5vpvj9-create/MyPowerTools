using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using MyPowerTools.Abstractions;
using MyPowerTools.Shell.Avalonia.ViewModels;

namespace MyPowerTools.Shell.Avalonia.Services;

internal sealed record ShellCommandFaultOwner(
    ShellCommandFaultSink Sink,
    ShellCommandFaultContext WorkspaceContext);

internal static class ShellCommandFaultOwnership
{
    private const int MaximumObjects = 4096;

    public static void Attach(
        object? root,
        ShellCommandFaultSink sink,
        ShellCommandFaultContext workspaceContext)
    {
        if (root is null)
        {
            return;
        }

        var pending = new Stack<object>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.Count > 0 && visited.Count < MaximumObjects)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (current is AsyncRelayCommand command)
            {
                command.SetFaultOwner(sink, workspaceContext);
                continue;
            }

            if (current is Control control)
            {
                if (control.DataContext is not null)
                {
                    pending.Push(control.DataContext);
                }
                continue;
            }

            if (current is string || current.GetType().IsValueType)
            {
                continue;
            }

            if (current is IEnumerable sequence)
            {
                try
                {
                    foreach (var item in sequence)
                    {
                        if (item is not null)
                        {
                            pending.Push(item);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ShellCommandFaultLog.Write("Attach command owner collection", ex, "ownership");
                }
                continue;
            }

            var type = current.GetType();
            if (type.Assembly != typeof(ShellCommandFaultOwnership).Assembly)
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                try
                {
                    if (property.GetValue(current) is { } value)
                    {
                        pending.Push(value);
                    }
                }
                catch (Exception ex)
                {
                    ShellCommandFaultLog.Write(
                        $"Attach command owner {type.Name}.{property.Name}",
                        ex,
                        "ownership");
                }
            }
        }
    }
}

internal sealed class ShellTerminalFaultRecovery
{
    private int _active;

    public void Reset()
    {
        Volatile.Write(ref _active, 0);
    }

    public bool TryRecover(
        Func<bool> isCurrent,
        Action showRecovery,
        Action showTerminalFallback)
    {
        if (!isCurrent() || Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            showRecovery();
        }
        catch (Exception recoveryException)
        {
            ShellCommandFaultLog.Write(
                "Render workspace recovery",
                recoveryException,
                "terminal-recovery");
            try
            {
                if (isCurrent())
                {
                    showTerminalFallback();
                }
            }
            catch (Exception terminalException)
            {
                ShellCommandFaultLog.Write(
                    "Render terminal workspace fallback",
                    terminalException,
                    "terminal-fallback");
            }
        }

        return true;
    }
}

internal static class ShellCommandFaultLog
{
    private const int MaximumLogBytes = 64 * 1024;
    private const int MaximumLineCharacters = 1024;
    private static readonly object Gate = new();
    private static readonly Regex SensitiveHeaderPattern = new(
        @"\b(authorization|cookie|set-cookie)\s*:\s*[^\r\n]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static void Write(string operation, Exception exception, string category)
    {
        try
        {
            var line = Format(operation, exception, category);
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyPowerTools",
                "logs");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "shell-faults.log");
            lock (Gate)
            {
                var additionBytes = Encoding.UTF8.GetByteCount(line + Environment.NewLine);
                if (File.Exists(path) && new FileInfo(path).Length + additionBytes > MaximumLogBytes)
                {
                    File.WriteAllText(path, "", Encoding.UTF8);
                }
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Fault diagnostics cannot become a second UI fault.
        }
    }

    internal static string Format(string operation, Exception exception, string category)
    {
        var safeOperation = OneLine(MptLogRedactor.Redact(operation));
        var safeMessage = OneLine(MptLogRedactor.Redact(exception.Message));
        var line = $"{DateTimeOffset.UtcNow:O}\t{OneLine(category)}\t{safeOperation}\t{exception.GetType().Name}\t{safeMessage}";
        return line.Length <= MaximumLineCharacters
            ? line
            : line[..MaximumLineCharacters];
    }

    private static string OneLine(string value)
    {
        var redacted = SensitiveHeaderPattern.Replace(value, "$1: ****");
        return redacted.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    }
}
