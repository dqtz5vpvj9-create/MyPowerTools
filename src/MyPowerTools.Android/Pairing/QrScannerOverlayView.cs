using Android.Content;
using Android.Util;
using Android.Views;
using Android.Graphics;
using A = Android;

namespace MyPowerTools.Android;

/// <summary>
/// The viewfinder decoration: a dimmed surround with a clear square and four corner marks. It draws
/// nothing that could be mistaken for a result — a decoded code is handled by the activity, and the
/// scanner never renders scanned text.
/// </summary>
internal sealed class QrScannerOverlayView : View
{
    private readonly Paint _dim = new() { Color = Color.Argb(150, 0, 0, 0) };
    private readonly Paint _corner = new()
    {
        Color = Color.Argb(255, 107, 165, 255),
        StrokeWidth = 6f,
        StrokeCap = Paint.Cap.Round
    };

    public QrScannerOverlayView(Context context) : base(context)
    {
        _corner.SetStyle(Paint.Style.Stroke);
        SetLayerType(LayerType.Hardware, null);
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        var width = Width;
        var height = Height;
        if (width <= 0 || height <= 0) return;

        var side = Math.Min(width, height) * 3 / 5;
        var left = (width - side) / 2f;
        var top = (height - side) / 2f;
        var right = left + side;
        var bottom = top + side;

        // Four rectangles around the window instead of a clipped path: no saveLayer, so the
        // overlay stays cheap on top of a live camera preview.
        canvas.DrawRect(0, 0, width, top, _dim);
        canvas.DrawRect(0, bottom, width, height, _dim);
        canvas.DrawRect(0, top, left, bottom, _dim);
        canvas.DrawRect(right, top, width, bottom, _dim);

        const float arm = 34f;
        canvas.DrawLine(left, top, left + arm, top, _corner);
        canvas.DrawLine(left, top, left, top + arm, _corner);
        canvas.DrawLine(right, top, right - arm, top, _corner);
        canvas.DrawLine(right, top, right, top + arm, _corner);
        canvas.DrawLine(left, bottom, left + arm, bottom, _corner);
        canvas.DrawLine(left, bottom, left, bottom - arm, _corner);
        canvas.DrawLine(right, bottom, right - arm, bottom, _corner);
        canvas.DrawLine(right, bottom, right, bottom - arm, _corner);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dim.Dispose();
            _corner.Dispose();
        }

        base.Dispose(disposing);
    }
}
