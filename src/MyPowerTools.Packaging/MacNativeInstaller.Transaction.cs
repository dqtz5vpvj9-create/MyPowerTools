using System.Globalization;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace MyPowerTools.Packaging.Ota;

public sealed partial class MacNativeInstaller
{
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(5);

    /// <summary>Physical install locations, resolved once per apply.</summary>
    private sealed record InstallLayout(
        string TargetApp,
        string ApplicationsRoot,
        string DataRoot,
        string InstallLockPath,
        string LaunchAgentsRoot,
        string LogsRoot,
        string UserId);

    private sealed record AgentState(string Label, string PlistPath, bool Existed, bool Loaded, byte[]? Bytes);

    private sealed record InstallOutcome(
        Exception? Failure,
        bool RolledBack,
        JsonObject? Health,
        IReadOnlyList<MacBundleProcess> StoppedProcesses);

    private InstallLayout PrepareLayout()
    {
        var requested = AppBundleFull;
        var bundleName = Path.GetFileName(requested);
        if (!bundleName.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"安装目标必须是 .app 应用包：{requested}");
        }

        var applicationsParent = Path.GetDirectoryName(requested)
            ?? throw new ArgumentException($"无法确定安装目标的上级目录：{requested}");
        Directory.CreateDirectory(applicationsParent);
        var applicationsRoot = MacNative.ResolvePhysicalPath(applicationsParent).TrimEnd('/');
        var targetApp = Path.Combine(applicationsRoot, bundleName);

        Directory.CreateDirectory(DataRootFull);
        var dataRoot = MacNative.ResolvePhysicalPath(DataRootFull).TrimEnd('/');
        if (string.Equals(dataRoot, targetApp, StringComparison.OrdinalIgnoreCase) ||
            dataRoot.StartsWith(targetApp + "/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("数据目录不能位于应用包内部。");
        }

        var lockPath = Path.Combine(applicationsRoot, ".mypowertools-install.lock");
        foreach (var protectedPath in new[] { targetApp, lockPath })
        {
            if (IsSymbolicLink(protectedPath))
            {
                throw new InvalidOperationException($"拒绝替换符号链接：{protectedPath}");
            }
        }

        if (File.Exists(targetApp))
        {
            throw new InvalidOperationException($"安装位置已被一个普通文件占用：{targetApp}");
        }

        var userId = MacNative.CurrentUserId().ToString(CultureInfo.InvariantCulture);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new InstallLayout(
            targetApp,
            applicationsRoot,
            dataRoot,
            lockPath,
            Path.Combine(home, "Library", "LaunchAgents"),
            Path.Combine(home, "Library", "Logs", "MyPowerTools"),
            userId);
    }

    /// <summary>
    /// Stages, validates and swaps in the new bundle. Returns a failure instead of throwing, so
    /// the caller can record it; only cancellation before the point of no return propagates.
    /// </summary>
    private Task<InstallOutcome> InstallBundleAsync(
        InstallLayout layout,
        string packagePath,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => InstallBundle(layout, packagePath, expectedVersion, cancellationToken), cancellationToken);
    }

    private InstallOutcome InstallBundle(
        InstallLayout layout,
        string packagePath,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var stageRoot = Path.Combine(layout.ApplicationsRoot, ".mypowertools-install." + id);
        var bundleStem = Path.GetFileNameWithoutExtension(layout.TargetApp);
        var backupApp = Path.Combine(
            layout.ApplicationsRoot,
            $"{bundleStem}.backup.{DateTime.Now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)}.{id}.app");
        var preserveStage = false;
        try
        {
            // Staging is a sibling on the destination volume, so the swap is two renames and a
            // broken download or bundle never touches the installed app.
            Report("stage", "正在解压安装包…");
            Directory.CreateDirectory(stageRoot);
            TrySetUnixMode(stageRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // ditto, not ZipFile: the archive is made with `ditto -c -k` and carries symlinks,
            // mode bits and extended attributes the code signature depends on.
            RunRequired("/usr/bin/ditto", ["-x", "-k", packagePath, stageRoot], TimeSpan.FromMinutes(15), "解压安装包");
            var stagedApp = FindStagedApp(stageRoot, Path.GetFileName(layout.TargetApp));
            MacProcessRunner.Run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", stagedApp], TimeSpan.FromMinutes(2));

            Report("verify", "正在校验应用包与代码签名…");
            AssertInstallBundle(stagedApp, expectedVersion);
            var agents = CollectAgentStates(layout);
            cancellationToken.ThrowIfCancellationRequested();

            // Point of no return: from here on cancellation is ignored so the swap either
            // completes or is rolled back.
            var outcome = SwapAndActivate(layout, stagedApp, backupApp, agents);
            preserveStage = outcome.Failure is not null && !outcome.RolledBack;
            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new InstallOutcome(exception, false, null, []);
        }
        finally
        {
            if (!preserveStage)
            {
                TryDeleteDirectory(stageRoot);
            }
        }
    }

    private InstallOutcome SwapAndActivate(
        InstallLayout layout,
        string stagedApp,
        string backupApp,
        IReadOnlyList<AgentState> agents)
    {
        var targetApp = layout.TargetApp;
        var hadInstallation = Directory.Exists(targetApp);
        var oldMoved = false;
        var newMoved = false;
        var activationStarted = false;
        IReadOnlyList<MacBundleProcess> stopped = [];
        try
        {
            if (Directory.Exists(backupApp))
            {
                throw new InvalidOperationException($"备份位置已存在：{backupApp}");
            }

            // Maintenance: the agents are KeepAlive, so leaving them loaded would let launchd
            // restart Runner against a half-replaced bundle.
            Report("stop", "正在停止 MyPowerTools 及后台服务…");
            foreach (var agent in agents.Where(agent => agent.Loaded))
            {
                BootOut(layout.UserId, agent.Label);
            }

            stopped = StopBundleProcesses(targetApp, layout.UserId);

            Report("swap", "正在替换应用…");
            if (Directory.Exists(targetApp))
            {
                Directory.Move(targetApp, backupApp);
                oldMoved = true;
            }

            Directory.Move(stagedApp, targetApp);
            newMoved = true;
            activationStarted = true;

            // Register identities before starting UserNotifications consumers.
            Report("services", "正在注册应用并启动后台服务…");
            RegisterAppBundles(targetApp);
            var macRoot = MacInstallLogic.MacRoot(targetApp);
            foreach (var label in MacInstallLogic.AgentLabels)
            {
                WriteLaunchAgent(
                    layout,
                    label,
                    MacInstallLogic.LaunchAgentProgramArguments(label, targetApp, layout.DataRoot),
                    macRoot);
            }

            Report("health", "正在检查后台服务是否正常…");
            var health = CheckHealth(targetApp, layout.UserId);
            if (health["ok"]?.GetValue<bool>() != true)
            {
                throw new InvalidOperationException(
                    $"健康检查未通过：{MacInstallLogic.ReadString(health["detail"])}（{MacInstallLogic.ReadString(health["endpoint"])}）。");
            }

            if (oldMoved)
            {
                TryDeleteDirectory(backupApp);
            }

            return new InstallOutcome(null, false, health, stopped);
        }
        catch (Exception failure)
        {
            Report("rollback", hadInstallation ? "安装失败，正在恢复之前的版本…" : "安装失败，正在撤销本次更改…");
            try
            {
                if (activationStarted)
                {
                    foreach (var label in MacInstallLogic.AgentLabels)
                    {
                        if (IsAgentLoaded(layout.UserId, label))
                        {
                            BootOut(layout.UserId, label);
                        }
                    }

                    StopBundleProcesses(targetApp, layout.UserId);
                }

                if (newMoved)
                {
                    Directory.Move(targetApp, stagedApp);
                }

                if (oldMoved)
                {
                    Directory.Move(backupApp, targetApp);
                }

                // The new plists point at executables that no longer exist after the restore.
                foreach (var agent in agents)
                {
                    if (agent.Existed)
                    {
                        File.WriteAllBytes(agent.PlistPath, agent.Bytes!);
                    }
                    else
                    {
                        TryDeleteFile(agent.PlistPath);
                    }
                }

                if (Directory.Exists(targetApp))
                {
                    RegisterAppBundles(targetApp);
                }

                foreach (var agent in agents.Where(agent => agent.Loaded))
                {
                    if (!IsAgentLoaded(layout.UserId, agent.Label))
                    {
                        RunRequired(
                            "/bin/launchctl",
                            ["bootstrap", $"gui/{layout.UserId}", agent.PlistPath],
                            TimeSpan.FromSeconds(30),
                            $"恢复后台服务 {agent.Label}");
                    }
                }

                if (hadInstallation && _options.Relaunch)
                {
                    Relaunch(targetApp);
                }

                var prefix = hadInstallation ? "安装失败，已恢复之前的版本" : "安装失败，已撤销本次更改";
                return new InstallOutcome(
                    new InvalidOperationException($"{prefix}：{failure.Message}", failure),
                    true,
                    null,
                    stopped);
            }
            catch (Exception rollbackFailure)
            {
                var message =
                    $"安装失败：{failure.Message}。回滚未完成：{rollbackFailure.Message}。" +
                    $"恢复路径：备份={backupApp}；新版本暂存={stagedApp}；目标={targetApp}";
                TryWriteRollbackMarker(message);
                return new InstallOutcome(new InvalidOperationException(message, failure), false, null, stopped);
            }
        }
    }

    private static string FindStagedApp(string stageRoot, string preferredName)
    {
        var preferred = Path.Combine(stageRoot, preferredName);
        if (Directory.Exists(preferred))
        {
            return preferred;
        }

        var canonical = Path.Combine(stageRoot, "MyPowerTools.app");
        if (Directory.Exists(canonical))
        {
            return canonical;
        }

        return Directory.GetDirectories(stageRoot, "*.app").OrderBy(path => path, StringComparer.Ordinal).FirstOrDefault()
            ?? throw new InvalidDataException("安装包中没有找到 .app 应用包。");
    }

    /// <summary>Assert-MacOSInstallBundle: identity, version, payload and code signature.</summary>
    private static void AssertInstallBundle(string app, string expectedVersion)
    {
        var plist = Path.Combine(app, "Contents", "Info.plist");
        if (!File.Exists(plist))
        {
            throw new InvalidDataException("应用包缺少 Contents/Info.plist。");
        }

        var plutilTimeout = TimeSpan.FromSeconds(30);
        RunRequired("/usr/bin/plutil", ["-lint", plist], plutilTimeout, "校验 Info.plist");
        var identifier = RunRequired(
            "/usr/bin/plutil", ["-extract", "CFBundleIdentifier", "raw", "-o", "-", plist], plutilTimeout, "读取应用标识")
            .StandardOutput.Trim();
        if (identifier != MacInstallLogic.BundleIdentifier)
        {
            throw new InvalidDataException($"应用包标识不正确：{identifier}");
        }

        var version = RunRequired(
            "/usr/bin/plutil", ["-extract", "CFBundleShortVersionString", "raw", "-o", "-", plist], plutilTimeout, "读取应用版本")
            .StandardOutput.Trim();
        if (!MacInstallLogic.IsValidVersion(version))
        {
            throw new InvalidDataException($"应用包版本号无效：{version}");
        }

        if (version != expectedVersion)
        {
            throw new InvalidDataException($"安装包中的版本为 {version}，但更新源声明的是 {expectedVersion}。");
        }

        const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        foreach (var relative in MacInstallLogic.RequiredExecutables)
        {
            var executable = Path.Combine(app, relative);
            if (!File.Exists(executable))
            {
                throw new InvalidDataException($"应用包缺少必需的可执行文件：{relative}");
            }

            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(executable) & anyExecute) == 0)
            {
                throw new InvalidDataException($"应用包中的文件不可执行：{relative}");
            }
        }

        foreach (var relative in MacInstallLogic.RequiredDirectories)
        {
            if (!Directory.Exists(Path.Combine(app, relative)))
            {
                throw new InvalidDataException($"应用包缺少必需的目录：{relative}");
            }
        }

        // Verify before anything is stopped; a bundle Gatekeeper would refuse must never
        // replace a working installation.
        RunRequired("/usr/bin/codesign", ["--verify", "--deep", "--strict", app], TimeSpan.FromMinutes(10), "验证应用代码签名");
    }

    private IReadOnlyList<AgentState> CollectAgentStates(InstallLayout layout)
    {
        Directory.CreateDirectory(layout.LaunchAgentsRoot);
        Directory.CreateDirectory(layout.LogsRoot);
        var states = new List<AgentState>();
        foreach (var label in MacInstallLogic.AgentLabels)
        {
            var plistPath = Path.Combine(layout.LaunchAgentsRoot, label + ".plist");
            if (IsSymbolicLink(plistPath))
            {
                throw new InvalidOperationException($"拒绝替换符号链接形式的后台服务配置：{plistPath}");
            }

            var exists = File.Exists(plistPath);
            var loaded = IsAgentLoaded(layout.UserId, label);
            if (loaded && !exists)
            {
                throw new InvalidOperationException($"后台服务 {label} 已加载，但找不到它的配置文件，无法安全替换。");
            }

            byte[]? bytes = null;
            if (exists)
            {
                var program = RunRequired(
                    "/usr/bin/plutil",
                    ["-extract", "ProgramArguments.0", "raw", "-o", "-", plistPath],
                    TimeSpan.FromSeconds(30),
                    $"读取后台服务 {label} 的配置").StandardOutput.Trim();
                if (!MacInstallLogic.IsInside(program, layout.TargetApp))
                {
                    throw new InvalidOperationException(
                        $"后台服务 {label} 属于另一个 MyPowerTools 安装（{program}）。" +
                        $"请先卸载那个安装，或选择同一安装位置（{layout.TargetApp}）后重试。");
                }

                bytes = File.ReadAllBytes(plistPath);
            }

            states.Add(new AgentState(label, plistPath, exists, loaded, bytes));
        }

        return states;
    }

    private static void WriteLaunchAgent(
        InstallLayout layout,
        string label,
        IReadOnlyList<string> programArguments,
        string workingDirectory)
    {
        var plistPath = Path.Combine(layout.LaunchAgentsRoot, label + ".plist");
        var temporary = $"{plistPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                MacInstallLogic.BuildLaunchAgentPlist(label, programArguments, workingDirectory, layout.LogsRoot),
                new System.Text.UTF8Encoding(false));
            RunRequired("/usr/bin/plutil", ["-lint", temporary], TimeSpan.FromSeconds(30), $"校验后台服务 {label} 的配置");
            File.Move(temporary, plistPath, overwrite: true);
            if (IsAgentLoaded(layout.UserId, label))
            {
                BootOut(layout.UserId, label);
            }

            RunRequired(
                "/bin/launchctl",
                ["bootstrap", $"gui/{layout.UserId}", plistPath],
                TimeSpan.FromSeconds(30),
                $"启动后台服务 {label}");
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static bool IsAgentLoaded(string userId, string label)
    {
        return MacProcessRunner.Run("/bin/launchctl", ["print", $"gui/{userId}/{label}"], TimeSpan.FromSeconds(30)).Succeeded;
    }

    /// <summary>Boots a job out and waits briefly until launchd no longer knows it.</summary>
    private static void BootOut(string userId, string label)
    {
        MacProcessRunner.Run("/bin/launchctl", ["bootout", $"gui/{userId}/{label}"], TimeSpan.FromSeconds(30));
        var deadline = DateTime.UtcNow + StopGracePeriod;
        while (DateTime.UtcNow < deadline && IsAgentLoaded(userId, label))
        {
            Thread.Sleep(200);
        }
    }

    private static void RegisterAppBundles(string targetApp)
    {
        if (!File.Exists(MacInstallLogic.LsRegisterPath))
        {
            return;
        }

        var bundles = new List<string> { targetApp };
        var helpers = Path.Combine(MacInstallLogic.MacRoot(targetApp), "Helpers");
        if (Directory.Exists(helpers))
        {
            bundles.AddRange(Directory.GetDirectories(helpers, "*.app").OrderBy(path => path, StringComparer.Ordinal));
        }

        foreach (var bundle in bundles)
        {
            MacProcessRunner.Run(MacInstallLogic.LsRegisterPath, ["-f", bundle], TimeSpan.FromSeconds(60));
        }
    }

    /// <summary>
    /// Stop-AppBundleProcesses: SIGTERM, then SIGKILL after the grace period, re-enumerating
    /// before each escalation. The current process (and any configured exclusions) survive.
    /// </summary>
    private IReadOnlyList<MacBundleProcess> StopBundleProcesses(string bundlePath, string userId)
    {
        var excluded = new HashSet<int>(_options.ExcludedProcessIds) { Environment.ProcessId };
        var initial = ListBundleProcesses(bundlePath, userId, excluded);
        if (initial.Count == 0)
        {
            return initial;
        }

        IReadOnlyList<MacBundleProcess> remaining = initial;
        foreach (var signal in new[] { "-TERM", "-KILL" })
        {
            foreach (var process in ListBundleProcesses(bundlePath, userId, excluded))
            {
                MacProcessRunner.Run(
                    "/bin/kill",
                    [signal, process.ProcessId.ToString(CultureInfo.InvariantCulture)],
                    TimeSpan.FromSeconds(10));
            }

            var deadline = DateTime.UtcNow + StopGracePeriod;
            do
            {
                Thread.Sleep(100);
                remaining = ListBundleProcesses(bundlePath, userId, excluded);
            }
            while (remaining.Count > 0 && DateTime.UtcNow < deadline);

            if (remaining.Count == 0)
            {
                return initial;
            }
        }

        throw new InvalidOperationException(
            "无法停止仍在运行的 MyPowerTools 进程：" +
            string.Join("、", remaining.Select(process => $"{process.ProcessId} {process.ExecutablePath}")));
    }

    private static IReadOnlyList<MacBundleProcess> ListBundleProcesses(
        string bundlePath,
        string userId,
        IReadOnlyCollection<int> excluded)
    {
        // comm, never args: a path in an editor's or terminal's arguments does not make that
        // process part of the bundle.
        var listing = RunRequired(
            "/bin/ps", ["-ww", "-u", userId, "-o", "pid=,comm="], TimeSpan.FromSeconds(15), "列出进程");
        return MacInstallLogic.SelectBundleProcesses(listing.StandardOutput, bundlePath, excluded);
    }

    /// <summary>
    /// Test-RunnerEndpoint plus a launchd check: the Runner agent must execute from the new
    /// bundle and answer on its HostControl socket within the health timeout.
    /// </summary>
    private static JsonObject CheckHealth(string targetApp, string userId)
    {
        var socketPath = Path.Combine(Path.GetTempPath(), MacInstallLogic.RunnerSocketFileName);
        var print = MacProcessRunner.Run(
            "/bin/launchctl",
            ["print", $"gui/{userId}/{MacInstallLogic.RunnerLabel}"],
            TimeSpan.FromSeconds(30));
        var program = print.Succeeded ? MacInstallLogic.ParseLaunchctlProgram(print.StandardOutput) : null;
        if (!MacInstallLogic.IsInside(program, targetApp))
        {
            return Health(false, socketPath, print.Succeeded
                ? $"Runner 后台服务的程序路径不在新应用内：{program ?? "(未知)"}"
                : $"Runner 后台服务未加载：{print.Diagnostic}");
        }

        var deadline = DateTime.UtcNow + HealthTimeout;
        var lastError = "socket file has not appeared";
        while (DateTime.UtcNow < deadline)
        {
            if (Path.Exists(socketPath))
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), attempt.Token)
                        .AsTask().GetAwaiter().GetResult();
                    return Health(true, socketPath, "connected");
                }
                catch (Exception exception) when (exception is SocketException or OperationCanceledException or IOException)
                {
                    lastError = exception.Message;
                }
            }
            else
            {
                lastError = "socket file has not appeared";
            }

            Thread.Sleep(500);
        }

        return Health(false, socketPath, $"Runner 未在 {HealthTimeout.TotalSeconds:0} 秒内响应：{lastError}");
    }

    private static JsonObject Health(bool ok, string endpoint, string detail) => new()
    {
        ["ok"] = ok,
        ["endpoint"] = endpoint,
        ["detail"] = detail
    };

    private static bool Relaunch(string targetApp)
    {
        return OperatingSystem.IsMacOS() &&
               Directory.Exists(targetApp) &&
               MacProcessRunner.Run("/usr/bin/open", [targetApp], TimeSpan.FromSeconds(30)).Succeeded;
    }

    private void TryWriteRollbackMarker(string message)
    {
        try
        {
            WriteTextAtomic(
                Path.Combine(StateRoot, "ROLLBACK-FAILED.txt"),
                $"macOS install/update failed at {UtcNow()}.\n{message}\n");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static MacProcessResult RunRequired(string fileName, string[] arguments, TimeSpan timeout, string activity)
    {
        var result = MacProcessRunner.Run(fileName, arguments, timeout);
        if (!result.Succeeded)
        {
            var detail = result.Diagnostic;
            throw new InvalidOperationException(
                $"{activity}失败（{Path.GetFileName(fileName)} 退出码 {result.ExitCode}）" +
                (string.IsNullOrEmpty(detail) ? "。" : $"：{detail}"));
        }

        return result;
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
