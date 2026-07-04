using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsGlobalHotkeyService : IHotkeyService
{
    private const uint ModNoRepeat = 0x4000;
    private const uint WmHotkey = 0x0312;
    private const uint WmAppCommand = 0x8001;
    private const uint PmNoRemove = 0x0000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private const int InitialHotkeyId = 0x4D50;

    private readonly ConcurrentQueue<HotkeyCommand> _commands = new();
    private readonly Dictionary<string, RegisteredHotkey> _registrationsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, RegisteredHotkey> _registrationsByNativeId = new();
    private readonly Dictionary<string, RegisteredHotkey> _registrationsByGesture = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _messageThread;
    private int _nextNativeId = InitialHotkeyId;
    private int _disposed;
    private uint _messageThreadId;

    public event EventHandler<HotkeyInvocation>? Pressed;

    public WindowsGlobalHotkeyService()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows global hotkeys require user32 RegisterHotKey.");
        }

        _messageThread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "MyPowerTools Windows hotkey loop"
        };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();
    }

    public async Task<HotkeyRegistrationResult> RegisterAsync(HotkeyRegistration registration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = ValidateRegistration(registration);
        if (validation is not null)
        {
            return validation;
        }

        if (!WindowsHotkeyGesture.TryParse(registration.Gesture, out var gesture, out var parseError))
        {
            return new HotkeyRegistrationResult(false, "validation-failed", parseError);
        }

        var command = HotkeyCommand.Register(registration, gesture!);
        return await EnqueueAsync(command, cancellationToken);
    }

    public async Task<HotkeyRegistrationResult> UnregisterAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(id))
        {
            return new HotkeyRegistrationResult(false, "validation-failed", "Hotkey id is required.");
        }

        var command = HotkeyCommand.Unregister(id);
        return await EnqueueAsync(command, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            Pressed = null;
            return;
        }

        var command = HotkeyCommand.Dispose();
        _commands.Enqueue(command);
        if (PostWakeMessage())
        {
            try
            {
                await command.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                // Process exit remains safe; registered hotkeys are scoped to this process thread.
            }
        }

        Pressed = null;
    }

    private async Task<HotkeyRegistrationResult> EnqueueAsync(HotkeyCommand command, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return new HotkeyRegistrationResult(false, "disposed", "Windows global hotkey service is disposed.");
        }

        await _ready.Task.WaitAsync(cancellationToken);
        if (Volatile.Read(ref _disposed) == 1)
        {
            return new HotkeyRegistrationResult(false, "disposed", "Windows global hotkey service is disposed.");
        }

        _commands.Enqueue(command);
        if (!PostWakeMessage())
        {
            var error = Marshal.GetLastWin32Error();
            command.Completion.TrySetResult(new HotkeyRegistrationResult(false, "failed", $"PostThreadMessage failed: {DescribeError(error)}"));
        }

        return await command.Completion.Task.WaitAsync(cancellationToken);
    }

    private void RunMessageLoop()
    {
        try
        {
            _messageThreadId = GetCurrentThreadId();
            PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
            _ready.TrySetResult();

            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Message == WmHotkey)
                {
                    RaiseHotkeyPressed(message.WParam.ToInt32());
                    continue;
                }

                if (message.Message == WmAppCommand)
                {
                    DrainCommands();
                    continue;
                }

                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            CompletePendingWithFailure(ex.Message);
        }
        finally
        {
            UnregisterAll();
        }
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            ProcessCommand(command);
        }
    }

    private void ProcessCommand(HotkeyCommand command)
    {
        switch (command.Operation)
        {
            case HotkeyOperation.Register:
                command.Completion.TrySetResult(RegisterOnMessageThread(command.Registration!, command.Gesture!));
                break;
            case HotkeyOperation.Unregister:
                command.Completion.TrySetResult(UnregisterOnMessageThread(command.Id!));
                break;
            case HotkeyOperation.Dispose:
                UnregisterAll();
                command.Completion.TrySetResult(new HotkeyRegistrationResult(true, "disposed", "Windows global hotkey service disposed."));
                PostQuitMessage(0);
                break;
        }
    }

    private HotkeyRegistrationResult RegisterOnMessageThread(HotkeyRegistration registration, WindowsHotkeyGesture gesture)
    {
        if (_registrationsByGesture.TryGetValue(gesture.NormalizedGesture, out var conflicting) &&
            !string.Equals(conflicting.Registration.Id, registration.Id, StringComparison.OrdinalIgnoreCase))
        {
            return new HotkeyRegistrationResult(
                false,
                "conflict",
                $"Hotkey '{gesture.NormalizedGesture}' is already registered for '{conflicting.Registration.Id}'.");
        }

        if (_registrationsById.ContainsKey(registration.Id))
        {
            UnregisterOnMessageThread(registration.Id);
        }

        var nativeId = _nextNativeId++;
        var modifiers = gesture.Modifiers | ModNoRepeat;
        if (!RegisterHotKey(IntPtr.Zero, nativeId, modifiers, gesture.VirtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            var state = error == ErrorHotkeyAlreadyRegistered ? "conflict" : "failed";
            return new HotkeyRegistrationResult(
                false,
                state,
                $"RegisterHotKey failed for '{gesture.NormalizedGesture}': {DescribeError(error)}");
        }

        var registered = new RegisteredHotkey(registration with { Gesture = gesture.NormalizedGesture }, gesture, nativeId);
        _registrationsById[registration.Id] = registered;
        _registrationsByNativeId[nativeId] = registered;
        _registrationsByGesture[gesture.NormalizedGesture] = registered;
        return new HotkeyRegistrationResult(true, "registered", $"Registered global hotkey '{gesture.NormalizedGesture}' for '{registration.Id}'.");
    }

    private HotkeyRegistrationResult UnregisterOnMessageThread(string id)
    {
        if (!_registrationsById.TryGetValue(id, out var registered))
        {
            return new HotkeyRegistrationResult(true, "not-registered", $"Hotkey '{id}' was not registered.");
        }

        _registrationsById.Remove(id);
        _registrationsByNativeId.Remove(registered.NativeId);
        _registrationsByGesture.Remove(registered.Gesture.NormalizedGesture);
        if (!UnregisterHotKey(IntPtr.Zero, registered.NativeId))
        {
            var error = Marshal.GetLastWin32Error();
            return new HotkeyRegistrationResult(false, "failed", $"UnregisterHotKey failed for '{id}': {DescribeError(error)}");
        }

        return new HotkeyRegistrationResult(true, "unregistered", $"Unregistered global hotkey '{registered.Gesture.NormalizedGesture}' for '{id}'.");
    }

    private void UnregisterAll()
    {
        foreach (var registered in _registrationsById.Values.ToArray())
        {
            UnregisterHotKey(IntPtr.Zero, registered.NativeId);
        }

        _registrationsById.Clear();
        _registrationsByNativeId.Clear();
        _registrationsByGesture.Clear();
    }

    private void RaiseHotkeyPressed(int nativeId)
    {
        if (!_registrationsByNativeId.TryGetValue(nativeId, out var registered))
        {
            return;
        }

        var invocation = new HotkeyInvocation(
            registered.Registration.Id,
            registered.Registration.Gesture,
            registered.Registration.Scope,
            DateTimeOffset.Now);

        var handler = Pressed;
        if (handler is null)
        {
            return;
        }

        foreach (EventHandler<HotkeyInvocation> subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, invocation);
            }
            catch
            {
                // Hotkey delivery stays alive even if one subscriber fails.
            }
        }
    }

    private void CompletePendingWithFailure(string message)
    {
        while (_commands.TryDequeue(out var command))
        {
            command.Completion.TrySetResult(new HotkeyRegistrationResult(false, "failed", message));
        }
    }

    private bool PostWakeMessage()
    {
        return _messageThreadId != 0 &&
            PostThreadMessage(_messageThreadId, WmAppCommand, IntPtr.Zero, IntPtr.Zero);
    }

    private static HotkeyRegistrationResult? ValidateRegistration(HotkeyRegistration registration)
    {
        if (string.IsNullOrWhiteSpace(registration.Id))
        {
            return new HotkeyRegistrationResult(false, "validation-failed", "Hotkey id is required.");
        }

        if (registration.Id.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_')))
        {
            return new HotkeyRegistrationResult(false, "validation-failed", "Hotkey id must contain only letters, digits, dot, dash, or underscore.");
        }

        if (string.IsNullOrWhiteSpace(registration.Scope))
        {
            return new HotkeyRegistrationResult(false, "validation-failed", "Hotkey scope is required.");
        }

        return null;
    }

    private static string DescribeError(int error)
    {
        return error == 0 ? "unknown Win32 error" : new Win32Exception(error).Message;
    }

    private sealed record RegisteredHotkey(HotkeyRegistration Registration, WindowsHotkeyGesture Gesture, int NativeId);

    private enum HotkeyOperation
    {
        Register,
        Unregister,
        Dispose
    }

    private sealed record HotkeyCommand(
        HotkeyOperation Operation,
        HotkeyRegistration? Registration,
        WindowsHotkeyGesture? Gesture,
        string? Id,
        TaskCompletionSource<HotkeyRegistrationResult> Completion)
    {
        public static HotkeyCommand Register(HotkeyRegistration registration, WindowsHotkeyGesture gesture)
        {
            return new HotkeyCommand(
                HotkeyOperation.Register,
                registration,
                gesture,
                null,
                new TaskCompletionSource<HotkeyRegistrationResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        public static HotkeyCommand Unregister(string id)
        {
            return new HotkeyCommand(
                HotkeyOperation.Unregister,
                null,
                null,
                id,
                new TaskCompletionSource<HotkeyRegistrationResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        public static HotkeyCommand Dispose()
        {
            return new HotkeyCommand(
                HotkeyOperation.Dispose,
                null,
                null,
                null,
                new TaskCompletionSource<HotkeyRegistrationResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, IntPtr hWnd, uint messageFilterMin, uint messageFilterMax);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr hWnd, uint messageFilterMin, uint messageFilterMax, uint removeMsg);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll", SetLastError = false)]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr HWnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
