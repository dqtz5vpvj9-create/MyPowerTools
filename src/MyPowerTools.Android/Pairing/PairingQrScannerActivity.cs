using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Content;
using MyPowerTools.Android.Pairing;
using MyPowerTools.Platform.Abstractions;
using A = Android;

namespace MyPowerTools.Android;

/// <summary>
/// The one place the app opens the camera. It is started only from an explicit "scan a connection
/// code" action, asks for <c>android.permission.CAMERA</c> at that moment (never at launch), and
/// hands back at most one classified link to <see cref="MobileQrScan"/>.
/// <para>
/// The activity is not exported, keeps no history, and shows no scanned content: a frame becomes a
/// string, the string is classified, and everything that is not a recognised <c>mpt://</c> link is
/// rejected here. The credential inside a link therefore never reaches a log, a toast, saved
/// instance state or another app. There is no WebView and no third-party scanner activity: the
/// preview and the decode both run in this process against the platform camera.
/// </para>
/// <para>
/// Every camera call is serialised on one camera thread with <see cref="PairingCodeSession"/> as the
/// gate, so a repeated <c>SurfaceChanged</c>, a resumed activity and a late
/// <c>CameraDevice.StateCallback</c> cannot stack a second <c>openCamera</c> or reopen a camera the
/// user already left. The activity carries the request token it was started with and only ever
/// resolves that token's request.
/// </para>
/// </summary>
[Activity(Label = "扫描连接码", Exported = false, NoHistory = true,
    Theme = "@style/Theme.MyPowerTools.Scanner",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode,
    ScreenOrientation = ScreenOrientation.Portrait)]
public sealed class PairingQrScannerActivity : global::AndroidX.Activity.ComponentActivity
{
    private const int CameraPermissionRequest = 61;
    private const string RequestIdExtra = "com.mypowertools.android.extra.SCAN_REQUEST_ID";
    private const string HintText = "把电脑端 MPT 显示的连接码二维码放进取景框";

    private readonly MptQrCodeDecoder _decoder = new();
    private readonly PairingCodeSession _session = new();
    private readonly object _frameGate = new();
    private HandlerThread? _cameraThread;
    private Handler? _cameraHandler;
    private CameraDevice? _camera;
    private CameraCaptureSession? _captureSession;
    private ImageReader? _reader;
    private CaptureRequest.Builder? _captureBuilder;
    private SessionStateCallback? _sessionCallback;
    private SurfaceView? _preview;
    private TextView? _caption;
    private long _requestId;
    private volatile bool _decoding;
    private volatile bool _completed;
    private int _rotationDegrees;
    private long _lastRejectionTicks;

    /// <summary>Launches the viewfinder and tags it with the request that owns the result.</summary>
    internal static void Start(Activity activity, long requestId)
    {
        var intent = new Intent(activity, typeof(PairingQrScannerActivity));
        intent.PutExtra(RequestIdExtra, requestId);
        activity.StartActivity(intent);
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _requestId = Intent?.GetLongExtra(RequestIdExtra, 0L) ?? 0L;
        OnBackPressedDispatcher.AddCallback(this, new BackCallback(this));

        if (ContextCompat.CheckSelfPermission(this, Manifest.Permission.Camera) != Permission.Granted)
        {
            // Asked here, inside the scan, and nowhere else. There is deliberately no countdown:
            // reading the permission dialog must not be able to cancel the scan. The answer arrives
            // through OnRequestPermissionsResult, and a user who leaves without answering ends the
            // activity, which resolves the request from OnDestroy.
            RequestPermissions([Manifest.Permission.Camera], CameraPermissionRequest);
            return;
        }

        BuildContentView();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != CameraPermissionRequest) return;
        var granted = grantResults.Length > 0 && grantResults[0] == Permission.Granted;
        // Harmless when this activity never waited on the permission (the request can already be
        // gone, or an earlier scan owned it): the registry ignores a token that is not current.
        MobileQrScan.Default.CompletePermissionRequest(_requestId, granted);
        if (granted)
        {
            BuildContentView();
            OpenCamera();
            return;
        }

        Complete(MobileQrScanResult.Cancelled(
            "扫描需要相机权限。可以在系统设置里为 MyPowerTools 打开相机权限后重试。",
            openManualEntry: true,
            error: MobileQrScanError.PermissionDenied));
    }

    private void BuildContentView()
    {
        if (_preview is not null) return;

        var root = new FrameLayout(this);

        var preview = new SurfaceView(this);
        root.AddView(preview, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        preview.Holder!.AddCallback(new SurfaceCallback(this));
        _preview = preview;

        var overlay = new QrScannerOverlayView(this);
        root.AddView(overlay, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        var caption = new TextView(this) { Text = HintText };
        caption.SetTextColor(Color.White);
        caption.SetTextSize(ComplexUnitType.Sp, 15f);
        caption.SetShadowLayer(6f, 0f, 1f, Color.Black);
        var captionLayout = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.CenterHorizontal | GravityFlags.Bottom
        };
        captionLayout.BottomMargin = Dp(116);
        root.AddView(caption, captionLayout);
        _caption = caption;

        // A camera can be unavailable or unusable, so the path that never needs it is always one tap
        // away instead of being hidden behind a failure.
        var manual = new Button(this) { Text = "手工输入连接码" };
        manual.Click += (_, _) => Complete(MobileQrScanResult.Cancelled(
            "请回到原来的页面，在“添加设备”里粘贴连接码。", openManualEntry: true));
        root.AddView(manual, BottomLayout(60));

        var cancel = new Button(this) { Text = "取消" };
        cancel.Click += (_, _) => Complete(MobileQrScanResult.Cancelled("已取消扫描。"));
        root.AddView(cancel, BottomLayout(8));

        SetContentView(root);
    }

    private FrameLayout.LayoutParams BottomLayout(int bottomDp) => new(
        ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
    {
        Gravity = GravityFlags.CenterHorizontal | GravityFlags.Bottom,
        BottomMargin = Dp(bottomDp)
    };

    private int Dp(int value) => (int)Math.Round(value * (Resources?.DisplayMetrics?.Density ?? 1f));

    private sealed class SurfaceCallback(PairingQrScannerActivity owner) : Java.Lang.Object, ISurfaceHolderCallback
    {
        public void SurfaceCreated(ISurfaceHolder holder) => owner.OnSurfaceReady();

        // Android raises SurfaceChanged for size and format changes too; the session gate makes the
        // repeated call harmless instead of a second openCamera.
        public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height) => owner.OnSurfaceReady();

        public void SurfaceDestroyed(ISurfaceHolder holder) => owner.OnSurfaceGone();
    }

    /// <summary>
    /// The camera thread serialises every camera operation. Android delivers surface, lifecycle and
    /// camera callbacks on different threads, and a state machine mutated from several threads is
    /// what let a second <c>openCamera</c> race the first.
    /// </summary>
    private Handler CameraHandler
    {
        get
        {
            if (_cameraHandler is null)
            {
                _cameraThread ??= new HandlerThread("mpt-qr-camera");
                if (!_cameraThread.IsAlive) _cameraThread.Start();
                _cameraHandler = new Handler(_cameraThread.Looper!);
            }

            return _cameraHandler;
        }
    }

    /// <summary>
    /// One hop to the camera thread for both the state change and the open decision it implies: if the
    /// two were posted separately, a lifecycle event arriving in between could abandon the surface and
    /// still leave the open request running against it.
    /// </summary>
    private void OnSurfaceReady() => CameraHandler.Post(() =>
    {
        _session.OnSurfaceReady(_preview?.Holder?.Surface?.IsValid == true);
        OpenCameraOnCameraThread();
    });

    private void OnSurfaceGone() => CameraHandler.Post(() =>
    {
        _session.OnSurfaceDestroyed();
        CloseCameraOnCameraThread();
    });

    private void OpenCamera() => CameraHandler.Post(OpenCameraOnCameraThread);

    private void OpenCameraOnCameraThread()
    {
        if (_session.TryBeginOpen() is not { } attempt) return;

        var manager = (CameraManager?)GetSystemService(CameraService);
        if (manager is null)
        {
            _session.MarkClosed(attempt);
            Complete(MobileQrScanResult.Failed("这台设备没有可用的相机服务。"));
            return;
        }

        try
        {
            var id = PickCameraId(manager);
            if (id is null)
            {
                _session.MarkClosed(attempt);
                Complete(MobileQrScanResult.Failed("没有找到可用的后置相机。"));
                return;
            }

            var characteristics = manager.GetCameraCharacteristics(id);
            var sensorOrientation = ReadInt(characteristics.Get(CameraCharacteristics.SensorOrientation)) ?? 90;
            var rotation = CurrentSurfaceRotation();
            _rotationDegrees = ((sensorOrientation - ((int)rotation * 90)) % 360 + 360) % 360;

            var map = (StreamConfigurationMap?)characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
            var size = PickAnalysisSize(map);
            var reader = ImageReader.NewInstance(size.Width, size.Height, ImageFormatType.Yuv420888, 2);
            reader.SetOnImageAvailableListener(new FrameListener(this), CameraHandler);
            _reader = reader;

            _sessionCallback = new SessionStateCallback(this, attempt);
            manager.OpenCamera(id, new CameraStateCallback(this, attempt), CameraHandler);
        }
        catch (Exception ex)
        {
            _session.MarkClosed(attempt);
            MobileQrScanLog.Error("camera-open", ex);
            Complete(MobileQrScanResult.Failed("无法打开相机，请稍后重试。"));
        }
    }

    /// <summary>
    /// The rotation the display currently shows, which is what the camera frame has to be turned by.
    /// <c>Context.Display</c> only exists from API 30, and this app supports API 29, so the legacy
    /// window-manager display is the fallback there. The activity is locked to portrait, but a device
    /// that reports a landscape-rotated display (a foldable, a tablet in a keyboard case) would
    /// otherwise get a rotated analysis buffer and never decode.
    /// </summary>
    private SurfaceOrientation CurrentSurfaceRotation()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            return Display?.Rotation ?? SurfaceOrientation.Rotation0;
        }

#pragma warning disable CA1422 // The legacy display property is the only API 29 source of rotation.
        return WindowManager?.DefaultDisplay?.Rotation ?? SurfaceOrientation.Rotation0;
#pragma warning restore CA1422
    }

    /// <summary>
    /// The decoder does not need a large preview, so the smallest YUV size at or above 640x480 keeps
    /// each frame small enough to rotate and scan at frame rate.
    /// </summary>
    private static Size PickAnalysisSize(StreamConfigurationMap? map)
    {
        var fallback = new Size(1280, 720);
        if (map is null) return fallback;
        try
        {
            var sizes = map.GetOutputSizes((int)ImageFormatType.Yuv420888);
            if (sizes is null || sizes.Length == 0) return fallback;
            Size? best = null;
            foreach (var size in sizes)
            {
                if (size.Width < 640 || size.Height < 480) continue;
                if (best is null || (long)size.Width * size.Height < (long)best.Width * best.Height) best = size;
            }

            return best ?? sizes[0];
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static string? PickCameraId(CameraManager manager)
    {
        try
        {
            var ids = manager.GetCameraIdList();
            if (ids is null || ids.Length == 0) return null;
            foreach (var id in ids)
            {
                var characteristics = manager.GetCameraCharacteristics(id);
                if (ReadInt(characteristics.Get(CameraCharacteristics.LensFacing)) == (int)LensFacing.Back)
                {
                    return id;
                }
            }

            return ids[0];
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? ReadInt(Java.Lang.Object? value)
    {
        if (value is null) return null;
        try
        {
            return Convert.ToInt32(value.ToString(), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Runs on the camera thread; a camera the user already left is closed, not previewed.</summary>
    private void OnCameraOpened(CameraDevice camera, long attempt)
    {
        if (!_session.MarkOpened(attempt))
        {
            try { camera.Close(); } catch (Exception) { }
            try { camera.Dispose(); } catch (Exception) { }
            return;
        }

        _camera = camera;
        try
        {
            var surfaces = new List<Surface>();
            if (_preview?.Holder?.Surface is { IsValid: true } previewSurface) surfaces.Add(previewSurface);
            if (_reader?.Surface is { } analysisSurface) surfaces.Add(analysisSurface);
            if (surfaces.Count == 0)
            {
                _session.MarkClosed(attempt);
                Complete(MobileQrScanResult.Failed("相机预览不可用。"));
                return;
            }

            _captureBuilder = camera.CreateCaptureRequest(CameraTemplate.Preview)!;
            foreach (var surface in surfaces) _captureBuilder.AddTarget(surface);
            _captureBuilder.Set(CaptureRequest.ControlMode!, (int)ControlMode.Auto);
            _captureBuilder.Set(CaptureRequest.ControlAfMode!, (int)ControlAFMode.ContinuousPicture);

            // The SessionConfiguration overload exists from API 28, but it needs an Executor, and the
            // platform Handler is not one in this binding. The list overload is deprecated on API 30
            // and still functional on every level this app supports.
#pragma warning disable CA1422
            camera.CreateCaptureSession(surfaces, _sessionCallback!, CameraHandler);
#pragma warning restore CA1422
        }
        catch (Exception ex)
        {
            _session.MarkClosed(attempt);
            MobileQrScanLog.Error("camera-session", ex);
            Complete(MobileQrScanResult.Failed("相机无法开始预览。"));
        }
    }

    /// <summary>
    /// Runs on the camera thread. A session configured after the scan ended, after a pause, or for an
    /// attempt that was already abandoned is closed instead of streaming.
    /// </summary>
    private void OnSessionConfigured(CameraCaptureSession session, long attempt)
    {
        if (!_session.IsCurrentAttempt(attempt) || _captureBuilder is null)
        {
            try { session.Close(); } catch (Exception) { }
            try { session.Dispose(); } catch (Exception) { }
            return;
        }

        _captureSession = session;
        try
        {
            session.SetRepeatingRequest(_captureBuilder.Build()!, null, CameraHandler);
        }
        catch (Exception ex)
        {
            MobileQrScanLog.Error("camera-repeating", ex);
        }
    }

    private void OnFrame(Image image)
    {
        try
        {
            if (_completed || _decoding) return;
            lock (_frameGate)
            {
                if (_decoding) return;
                _decoding = true;
            }

            try
            {
                var plane = image.GetPlanes()?[0];
                var buffer = plane?.Buffer;
                if (plane is null || buffer is null) return;

                var pixels = new byte[buffer.Remaining()];
                buffer.Get(pixels, 0, pixels.Length);
                var text = _decoder.DecodeRotated(pixels, image.Width, image.Height, plane.RowStride,
                    _rotationDegrees, out _, out _);
                if (text is null) return;

                if (!MobileQrPayload.TryClassify(text, out var code, out var reason))
                {
                    ShowRejection(reason);
                    return;
                }

                Complete(MobileQrScanResult.Success(code!));
            }
            finally
            {
                lock (_frameGate) _decoding = false;
            }
        }
        catch (Exception ex)
        {
            _decoding = false;
            MobileQrScanLog.Error("camera-frame", ex);
        }
        finally
        {
            image.Close();
        }
    }

    /// <summary>
    /// A wrong QR code is normal while someone moves the phone, so the reason is shown at most once
    /// every few seconds instead of on every frame. The text never repeats the scanned payload.
    /// </summary>
    private void ShowRejection(string? reason)
    {
        if (reason is null) return;
        var now = System.Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastRejectionTicks) < 4000) return;
        Interlocked.Exchange(ref _lastRejectionTicks, now);
        RunOnUiThread(() =>
        {
            if (_completed || _caption is null) return;
            _caption.Text = reason;
            _caption.PostDelayed(() =>
            {
                if (!_completed && _caption is not null) _caption.Text = HintText;
            }, 3500);
        });
    }

    /// <summary>
    /// Resolves the request this activity was started for, at most once. The camera session closes
    /// first, so nothing can reopen a camera after the result is delivered. Safe to call from any
    /// thread: the toast and <see cref="Activity.Finish"/> are marshalled to the UI thread, and the
    /// token check means an older activity cannot complete a newer scan.
    /// </summary>
    private void Complete(MobileQrScanResult result)
    {
        if (_completed) return;
        _completed = true;
        _session.Close();
        CloseCamera();

        RunOnUiThread(() =>
        {
            if (result.Message is { } message)
            {
                // App-authored text about the scan; never scanned content.
                Toast.MakeText(this, message, ToastLength.Long)?.Show();
            }

            MobileQrScan.Default.Complete(_requestId, result);
            Finish();
        });
    }

    private void CloseCamera() => CameraHandler.Post(CloseCameraOnCameraThread);

    private void CloseCameraOnCameraThread()
    {
        var session = Interlocked.Exchange(ref _captureSession, null);
        var camera = Interlocked.Exchange(ref _camera, null);
        var reader = Interlocked.Exchange(ref _reader, null);
        var builder = Interlocked.Exchange(ref _captureBuilder, null);
        _ = builder;
        _session.MarkCameraLost();
        try { session?.Close(); } catch (Exception) { }
        try { session?.Dispose(); } catch (Exception) { }
        try { camera?.Close(); } catch (Exception) { }
        try { camera?.Dispose(); } catch (Exception) { }
        try { reader?.Close(); } catch (Exception) { }
        try { reader?.Dispose(); } catch (Exception) { }
    }

    protected override void OnResume()
    {
        base.OnResume();
        CameraHandler.Post(() =>
        {
            if (!_session.IsActive) return;
            _session.OnResumed();
            if (_preview?.Holder?.Surface?.IsValid == true) _session.OnSurfaceReady(true);
            OpenCameraOnCameraThread();
        });
    }

    protected override void OnPause()
    {
        // Closes the session before the camera is released, so a camera that answers after this point
        // is refused instead of previewed; the actual release stays on the camera thread.
        CameraHandler.Post(() =>
        {
            _session.OnPaused();
            CloseCameraOnCameraThread();
        });
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        _session.Close();
        CloseCamera();
        var thread = Interlocked.Exchange(ref _cameraThread, null);
        Interlocked.Exchange(ref _cameraHandler, null);
        if (thread is not null)
        {
            try { thread.QuitSafely(); } catch (Exception) { }
        }

        if (!_completed)
        {
            // The system destroyed the activity, or the user left it. Resolve only this request,
            // through its token, instead of leaving its caller pending forever.
            _completed = true;
            MobileQrScan.Default.Complete(_requestId, MobileQrScanResult.Cancelled(
                "扫描已中断。", openManualEntry: true, error: MobileQrScanError.Interrupted));
        }

        base.OnDestroy();
    }

    private sealed class BackCallback(PairingQrScannerActivity owner) : global::AndroidX.Activity.OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed() =>
            owner.Complete(MobileQrScanResult.Cancelled("已取消扫描。"));
    }

    private sealed class CameraStateCallback(PairingQrScannerActivity owner, long attempt) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera) => owner.OnCameraOpened(camera, attempt);

        public override void OnDisconnected(CameraDevice camera)
        {
            camera.Close();
            // A camera that was already abandoned reports nothing: the user has moved on, and the
            // attempt it belonged to is gone.
            if (!owner._session.IsCurrentAttempt(attempt)) return;
            owner._session.MarkClosed(attempt);
            owner.Complete(MobileQrScanResult.Failed("相机被其他应用占用。"));
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            camera.Close();
            if (!owner._session.IsCurrentAttempt(attempt)) return;
            owner._session.MarkClosed(attempt);
            owner.Complete(MobileQrScanResult.Failed("相机出错（" + error + "）。"));
        }
    }

    private sealed class SessionStateCallback(PairingQrScannerActivity owner, long attempt) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session) => owner.OnSessionConfigured(session, attempt);

        public override void OnConfigureFailed(CameraCaptureSession session)
        {
            if (!owner._session.IsCurrentAttempt(attempt)) return;
            owner._session.MarkClosed(attempt);
            owner.Complete(MobileQrScanResult.Failed("相机预览配置失败。"));
        }
    }

    private sealed class FrameListener(PairingQrScannerActivity owner) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        public void OnImageAvailable(ImageReader? reader)
        {
            if (reader is null) return;
            using var image = reader.AcquireLatestImage();
            if (image is null) return;
            owner.OnFrame(image);
        }
    }
}

/// <summary>
/// Scanner diagnostics. Scanned content never reaches this type: only the app-authored stage name and
/// an exception may be recorded (see <see cref="MobileQrPayload.Describe"/> for the only log-safe
/// representation of a link).
/// </summary>
internal static class MobileQrScanLog
{
    internal static void Error(string stage, Exception error) => AndroidStartupLog.Error(stage, error);
}
