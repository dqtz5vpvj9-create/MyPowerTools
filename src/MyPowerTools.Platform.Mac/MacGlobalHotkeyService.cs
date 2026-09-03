using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Mac;

/// <summary>
/// System-wide hotkeys backed by Carbon's RegisterEventHotKey. The Runner has no NSApplication
/// event loop, so the service owns a dedicated background thread that installs the Carbon event
/// handler on its own dispatcher target and then blocks in CFRunLoopRun. Registration and
/// unregistration are marshalled onto that thread through a CFRunLoopSource, the same way the
/// Windows service marshals through PostThreadMessage.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacGlobalHotkeyService : IHotkeyService
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint HotkeySignature = 0x4D505448;      // 'MPTH'
    private const uint EventClassKeyboard = 0x6B657962;   // 'keyb'
    private const uint EventHotKeyPressed = 5;
    private const uint ParamDirectObject = 0x2D2D2D2D;    // '----'
    private const uint TypeEventHotKeyId = 0x686B6964;    // 'hkid'
    private const int NoErr = 0;
    private const int EventNotHandledErr = -9874;
    private const int EventHotKeyExistsErr = -9878;
    private const int InitialHotkeyId = 1;

    private static readonly RunLoopPerformCallback PerformCallback = OnRunLoopPerform;
    private static readonly CarbonEventCallback HotkeyCallback = OnHotkeyEvent;
    private static readonly Lazy<nint> RunLoopCommonModes = new(LoadRunLoopCommonModes);

    private readonly ConcurrentQueue<HotkeyCommand> _commands = new();
    private readonly Dictionary<string, RegisteredHotkey> _registrationsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, RegisteredHotkey> _registrationsByNativeId = new();
    private readonly Dictionary<string, RegisteredHotkey> _registrationsByGesture = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _runLoopThread;
    private GCHandle _selfHandle;
    private nint _runLoop;
    private nint _wakeSource;
    private nint _eventHandler;
    private uint _nextNativeId = InitialHotkeyId;
    private int _disposed;

    public event EventHandler<HotkeyInvocation>? Pressed;

    public MacGlobalHotkeyService()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("macOS global hotkeys require Carbon RegisterEventHotKey.");
        }

        _runLoopThread = new Thread(RunHotkeyLoop)
        {
            IsBackground = true,
            Name = "MyPowerTools macOS hotkey loop"
        };
        _runLoopThread.Start();
    }

    public async Task<HotkeyRegistrationResult> RegisterAsync(HotkeyRegistration registration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = ValidateRegistration(registration);
        if (validation is not null)
        {
            return validation;
        }

        if (!MacHotkeyGesture.TryParse(registration.Gesture, out var gesture, out var parseError))
        {
            return new HotkeyRegistrationResult(false, "validation-failed", parseError);
        }

        return await EnqueueAsync(HotkeyCommand.Register(registration, gesture!), cancellationToken);
    }

    public async Task<HotkeyRegistrationResult> UnregisterAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(id))
        {
            return new HotkeyRegistrationResult(false, "validation-failed", "Hotkey id is required.");
        }

        return await EnqueueAsync(HotkeyCommand.Unregister(id), cancellationToken);
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
        if (SignalRunLoop())
        {
            try
            {
                await command.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                // Hotkeys die with the process; a stuck run loop must not block shutdown.
            }
        }

        Pressed = null;
    }

    private async Task<HotkeyRegistrationResult> EnqueueAsync(HotkeyCommand command, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return new HotkeyRegistrationResult(false, "disposed", "macOS global hotkey service is disposed.");
        }

        try
        {
            await _ready.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A Runner started outside a GUI session cannot install the Carbon handler. Report it
            // instead of tearing down Runner startup with the run loop thread's exception.
            return new HotkeyRegistrationResult(
                false,
                "failed",
                $"The macOS hotkey run loop failed to start: {ex.GetBaseException().Message}");
        }

        if (Volatile.Read(ref _disposed) == 1)
        {
            return new HotkeyRegistrationResult(false, "disposed", "macOS global hotkey service is disposed.");
        }

        _commands.Enqueue(command);
        if (!SignalRunLoop())
        {
            command.Completion.TrySetResult(new HotkeyRegistrationResult(
                false,
                "failed",
                "The macOS hotkey run loop is not accepting commands."));
        }

        return await command.Completion.Task.WaitAsync(cancellationToken);
    }

    private void RunHotkeyLoop()
    {
        nint wakeSource = 0;
        try
        {
            _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
            _runLoop = CFRunLoopGetCurrent();

            var sourceContext = new CFRunLoopSourceContext
            {
                Info = GCHandle.ToIntPtr(_selfHandle),
                Perform = Marshal.GetFunctionPointerForDelegate(PerformCallback)
            };
            wakeSource = CFRunLoopSourceCreate(0, 0, ref sourceContext);
            if (wakeSource == 0)
            {
                throw new InvalidOperationException("CFRunLoopSourceCreate returned a null source.");
            }

            CFRunLoopAddSource(_runLoop, wakeSource, RunLoopCommonModes.Value);
            _wakeSource = wakeSource;

            // Creating this thread's Carbon event queue installs its run loop source, so hot key
            // events dispatched to this thread's target are delivered by CFRunLoopRun below.
            GetCurrentEventQueue();
            var eventTypes = new[]
            {
                new EventTypeSpec { EventClass = EventClassKeyboard, EventKind = EventHotKeyPressed }
            };
            var installStatus = InstallEventHandler(
                GetEventDispatcherTarget(),
                Marshal.GetFunctionPointerForDelegate(HotkeyCallback),
                1,
                eventTypes,
                GCHandle.ToIntPtr(_selfHandle),
                out _eventHandler);
            if (installStatus != NoErr)
            {
                throw new InvalidOperationException($"InstallEventHandler failed with OSStatus {installStatus}.");
            }

            _ready.TrySetResult();
            CFRunLoopRun();
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            CompletePendingWithFailure(ex.Message);
        }
        finally
        {
            UnregisterAll();
            if (_eventHandler != 0)
            {
                RemoveEventHandler(_eventHandler);
                _eventHandler = 0;
            }

            _wakeSource = 0;
            if (wakeSource != 0)
            {
                CFRunLoopRemoveSource(_runLoop, wakeSource, RunLoopCommonModes.Value);
                CFRelease(wakeSource);
            }

            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
            }
        }
    }

    private bool SignalRunLoop()
    {
        var wakeSource = _wakeSource;
        var runLoop = _runLoop;
        if (wakeSource == 0 || runLoop == 0)
        {
            return false;
        }

        CFRunLoopSourceSignal(wakeSource);
        CFRunLoopWakeUp(runLoop);
        return true;
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
                command.Completion.TrySetResult(RegisterOnRunLoopThread(command.Registration!, command.Gesture!));
                break;
            case HotkeyOperation.Unregister:
                command.Completion.TrySetResult(UnregisterOnRunLoopThread(command.Id!));
                break;
            case HotkeyOperation.Dispose:
                UnregisterAll();
                command.Completion.TrySetResult(new HotkeyRegistrationResult(true, "disposed", "macOS global hotkey service disposed."));
                CFRunLoopStop(_runLoop);
                break;
        }
    }

    private HotkeyRegistrationResult RegisterOnRunLoopThread(HotkeyRegistration registration, MacHotkeyGesture gesture)
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
            UnregisterOnRunLoopThread(registration.Id);
        }

        var nativeId = _nextNativeId++;
        var hotKeyId = new EventHotKeyID { Signature = HotkeySignature, Id = nativeId };
        var status = RegisterEventHotKey(
            gesture.MacKeyCode,
            gesture.CarbonModifiers,
            hotKeyId,
            GetEventDispatcherTarget(),
            0,
            out var hotKeyRef);
        if (status != NoErr || hotKeyRef == 0)
        {
            var state = status == EventHotKeyExistsErr ? "conflict" : "failed";
            return new HotkeyRegistrationResult(
                false,
                state,
                $"RegisterEventHotKey failed for '{gesture.NormalizedGesture}' with OSStatus {status}.");
        }

        var registered = new RegisteredHotkey(
            registration with { Gesture = gesture.NormalizedGesture },
            gesture,
            nativeId,
            hotKeyRef);
        _registrationsById[registration.Id] = registered;
        _registrationsByNativeId[nativeId] = registered;
        _registrationsByGesture[gesture.NormalizedGesture] = registered;

        var message = $"Registered global hotkey '{gesture.NormalizedGesture}' for '{registration.Id}'.";
        if (!MacAccessibility.IsTrusted())
        {
            message += $" 该快捷键触发的按键注入尚不可用：{MacAccessibility.PermissionHint}";
        }

        return new HotkeyRegistrationResult(true, "registered", message);
    }

    private HotkeyRegistrationResult UnregisterOnRunLoopThread(string id)
    {
        if (!_registrationsById.TryGetValue(id, out var registered))
        {
            return new HotkeyRegistrationResult(true, "not-registered", $"Hotkey '{id}' was not registered.");
        }

        _registrationsById.Remove(id);
        _registrationsByNativeId.Remove(registered.NativeId);
        _registrationsByGesture.Remove(registered.Gesture.NormalizedGesture);
        var status = UnregisterEventHotKey(registered.HotKeyRef);
        if (status != NoErr)
        {
            return new HotkeyRegistrationResult(
                false,
                "failed",
                $"UnregisterEventHotKey failed for '{id}' with OSStatus {status}.");
        }

        return new HotkeyRegistrationResult(
            true,
            "unregistered",
            $"Unregistered global hotkey '{registered.Gesture.NormalizedGesture}' for '{id}'.");
    }

    private void UnregisterAll()
    {
        foreach (var registered in _registrationsById.Values.ToArray())
        {
            UnregisterEventHotKey(registered.HotKeyRef);
        }

        _registrationsById.Clear();
        _registrationsByNativeId.Clear();
        _registrationsByGesture.Clear();
    }

    private int HandleHotkeyEvent(nint theEvent)
    {
        var status = GetEventParameter(
            theEvent,
            ParamDirectObject,
            TypeEventHotKeyId,
            0,
            (nuint)Marshal.SizeOf<EventHotKeyID>(),
            0,
            out var hotKeyId);
        if (status != NoErr || hotKeyId.Signature != HotkeySignature)
        {
            return EventNotHandledErr;
        }

        RaiseHotkeyPressed(hotKeyId.Id);
        return NoErr;
    }

    private void RaiseHotkeyPressed(uint nativeId)
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

    private static MacGlobalHotkeyService? Resolve(nint context)
    {
        if (context == 0)
        {
            return null;
        }

        try
        {
            return GCHandle.FromIntPtr(context).Target as MacGlobalHotkeyService;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void OnRunLoopPerform(nint info)
    {
        Resolve(info)?.DrainCommands();
    }

    private static int OnHotkeyEvent(nint handlerCallRef, nint theEvent, nint userData)
    {
        var service = Resolve(userData);
        return service is null ? EventNotHandledErr : service.HandleHotkeyEvent(theEvent);
    }

    private static nint LoadRunLoopCommonModes()
    {
        var library = NativeLibrary.Load(CoreFoundation);
        return Marshal.ReadIntPtr(NativeLibrary.GetExport(library, "kCFRunLoopCommonModes"));
    }

    private sealed record RegisteredHotkey(
        HotkeyRegistration Registration,
        MacHotkeyGesture Gesture,
        uint NativeId,
        nint HotKeyRef);

    private enum HotkeyOperation
    {
        Register,
        Unregister,
        Dispose
    }

    private sealed record HotkeyCommand(
        HotkeyOperation Operation,
        HotkeyRegistration? Registration,
        MacHotkeyGesture? Gesture,
        string? Id,
        TaskCompletionSource<HotkeyRegistrationResult> Completion)
    {
        public static HotkeyCommand Register(HotkeyRegistration registration, MacHotkeyGesture gesture)
        {
            return new HotkeyCommand(HotkeyOperation.Register, registration, gesture, null, CreateCompletion());
        }

        public static HotkeyCommand Unregister(string id)
        {
            return new HotkeyCommand(HotkeyOperation.Unregister, null, null, id, CreateCompletion());
        }

        public static HotkeyCommand Dispose()
        {
            return new HotkeyCommand(HotkeyOperation.Dispose, null, null, null, CreateCompletion());
        }

        private static TaskCompletionSource<HotkeyRegistrationResult> CreateCompletion()
        {
            return new TaskCompletionSource<HotkeyRegistrationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RunLoopPerformCallback(nint info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CarbonEventCallback(nint handlerCallRef, nint theEvent, nint userData);

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyID
    {
        public uint Signature;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CFRunLoopSourceContext
    {
        public nint Version;
        public nint Info;
        public nint Retain;
        public nint Release;
        public nint CopyDescription;
        public nint Equal;
        public nint Hash;
        public nint Schedule;
        public nint Cancel;
        public nint Perform;
    }

    [DllImport(Carbon)]
    private static extern int RegisterEventHotKey(
        uint hotKeyCode,
        uint hotKeyModifiers,
        EventHotKeyID hotKeyId,
        nint target,
        uint options,
        out nint hotKeyRef);

    [DllImport(Carbon)]
    private static extern int UnregisterEventHotKey(nint hotKeyRef);

    [DllImport(Carbon)]
    private static extern nint GetEventDispatcherTarget();

    [DllImport(Carbon)]
    private static extern nint GetCurrentEventQueue();

    [DllImport(Carbon)]
    private static extern int InstallEventHandler(
        nint target,
        nint handler,
        nuint numTypes,
        EventTypeSpec[] typeList,
        nint userData,
        out nint handlerRef);

    [DllImport(Carbon)]
    private static extern int RemoveEventHandler(nint handlerRef);

    [DllImport(Carbon)]
    private static extern int GetEventParameter(
        nint theEvent,
        uint name,
        uint desiredType,
        nint actualType,
        nuint bufferSize,
        nint actualSize,
        out EventHotKeyID data);

    [DllImport(CoreFoundation)]
    private static extern nint CFRunLoopGetCurrent();

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopRun();

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopStop(nint runLoop);

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopWakeUp(nint runLoop);

    [DllImport(CoreFoundation)]
    private static extern nint CFRunLoopSourceCreate(nint allocator, nint order, ref CFRunLoopSourceContext context);

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopSourceSignal(nint source);

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopAddSource(nint runLoop, nint source, nint mode);

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopRemoveSource(nint runLoop, nint source, nint mode);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint value);
}
