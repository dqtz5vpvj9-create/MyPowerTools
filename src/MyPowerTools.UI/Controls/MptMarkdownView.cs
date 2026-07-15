using System.Net;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Styling;
using Markdig;
using TheArtOfDev.HtmlRenderer.Avalonia;

namespace MyPowerTools.UI.Controls;

public sealed partial class MptMarkdownView : HtmlLabel
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static readonly StyledProperty<string> MarkdownProperty =
        AvaloniaProperty.Register<MptMarkdownView, string>(nameof(Markdown), "");
    public static readonly StyledProperty<bool> IsPreviewProperty =
        AvaloniaProperty.Register<MptMarkdownView, bool>(nameof(IsPreview));

    private Point? _selectionStartPoint;
    private Point? _selectionEndPoint;
    private Point _lastClickPoint;
    private long _lastClickTimestamp;
    private int _consecutiveClickCount;

    public MptMarkdownView()
    {
        AutoSizeHeightOnly = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        IsSelectionEnabled = true;
        IsContextMenuEnabled = false;
        PointerPressed += InitializeSelectionAtPointerDown;
        PointerMoved += TrackSelectionPointer;
        ContextMenu = CreateSelectionContextMenu();
        ActualThemeVariantChanged += (_, _) => RenderMarkdown();
        RenderMarkdown();
    }

    public string Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value ?? "");
    }

    public bool IsPreview
    {
        get => GetValue(IsPreviewProperty);
        set => SetValue(IsPreviewProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty || change.Property == IsPreviewProperty)
        {
            RenderMarkdown();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs eventArgs)
    {
        Focus();
        base.OnPointerPressed(eventArgs);
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.C && eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            eventArgs.Handled = true;
            _ = CopySelectedTextAsync();
            return;
        }

        base.OnKeyDown(eventArgs);
    }

    private void InitializeSelectionAtPointerDown(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var pointerPosition = eventArgs.GetPosition(this);
            UpdateClickCount(pointerPosition);
            if (_consecutiveClickCount == 3)
            {
                SelectCurrentRenderedLine(pointerPosition, eventArgs);
                _consecutiveClickCount = 0;
                return;
            }

            _selectionStartPoint = _selectionEndPoint = pointerPosition;
            Container.HandleMouseMove(this, _selectionStartPoint.Value);
        }
    }

    private void UpdateClickCount(Point pointerPosition)
    {
        var now = Environment.TickCount64;
        var closeInTime = now - _lastClickTimestamp <= 500;
        var closeInSpace = Math.Abs(pointerPosition.X - _lastClickPoint.X) <= 4 &&
                           Math.Abs(pointerPosition.Y - _lastClickPoint.Y) <= 4;
        _consecutiveClickCount = closeInTime && closeInSpace ? _consecutiveClickCount + 1 : 1;
        _lastClickTimestamp = now;
        _lastClickPoint = pointerPosition;
    }

    private void SelectCurrentRenderedLine(Point pointerPosition, PointerPressedEventArgs eventArgs)
    {
        _selectionStartPoint = new Point(8, pointerPosition.Y);
        _selectionEndPoint = new Point(Math.Max(8, Bounds.Width - 8), pointerPosition.Y);
        Container.ClearSelection();
        Container.HandleLeftMouseDown(this, eventArgs);
        Container.HandleMouseMove(this, _selectionStartPoint.Value);
        Container.HandleMouseMove(this, _selectionEndPoint.Value);
    }

    private void TrackSelectionPointer(object? sender, PointerEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _selectionEndPoint = eventArgs.GetPosition(this);
        }
    }

    private ContextMenu CreateSelectionContextMenu()
    {
        var copyItem = new MenuItem { Header = "Copy" };
        copyItem.Click += async (_, _) => await CopySelectedTextAsync();
        return new ContextMenu { ItemsSource = new[] { copyItem } };
    }

    private async Task CopySelectedTextAsync()
    {
        var selectedText = GetSelectedTextForClipboard();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (!string.IsNullOrEmpty(selectedText) && clipboard is not null)
        {
            await clipboard.SetTextAsync(selectedText);
        }
    }

    private string? GetSelectedTextForClipboard()
    {
        var titleMatch = LeadingTitleRegex().Match(Markdown ?? "");
        if (titleMatch.Success &&
            _selectionStartPoint is { Y: <= 32 } &&
            _selectionEndPoint is { Y: <= 32 })
        {
            return titleMatch.Groups[1].Value;
        }

        try
        {
            return SelectedText;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private void RenderMarkdown()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        BaseStylesheet = (dark ? DarkCss : LightCss) + (IsPreview ? PreviewCss : "");
        var markdown = FormatLeadingTitle(Markdown ?? "");
        var html = Markdig.Markdown.ToHtml(markdown, Pipeline);
        html = ImageTagRegex().Replace(html, "");
        html = LinkTargetRegex().Replace(html, SanitizeLink);
        Text = $"<div id=\"write\">{html}</div>";
    }

    private static string SanitizeLink(Match match)
    {
        var encoded = match.Groups[1].Value;
        var target = WebUtility.HtmlDecode(encoded);
        return Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? $"href=\"{WebUtility.HtmlEncode(uri.AbsoluteUri)}\""
            : "";
    }

    private static string FormatLeadingTitle(string markdown)
    {
        return LeadingTitleRegex().Replace(markdown, "###### $1\n\n", 1);
    }

    [GeneratedRegex("<img\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageTagRegex();

    [GeneratedRegex("href=\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkTargetRegex();

    [GeneratedRegex("^\\s*(\\[[^\\]\\r\\n]+\\])(?!\\()\\s*", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingTitleRegex();

    private const string LightCss = """
        html, body {
          margin: 0;
          padding: 0;
          background-color: #ffffff;
          color: #333333;
          font-family: "Segoe UI Variable", "Microsoft YaHei UI", "Open Sans", "Helvetica Neue", Arial, sans-serif;
          font-size: 15px;
          line-height: 1.6;
        }
        #write { margin: 0; padding: 4px 8px 24px 8px; }
        p, blockquote, ul, ol, dl, table, pre { margin: 0.8em 0; }
        a { color: #4183c4; text-decoration: none; }
        h1, h2, h3, h4, h5, h6 {
          color: #24292f;
          font-weight: bold;
          line-height: 1.4;
          margin-top: 1em;
          margin-bottom: 1em;
        }
        h1 { font-size: 2.25em; line-height: 1.2; border-bottom: 1px solid #eeeeee; }
        h2 { font-size: 1.75em; line-height: 1.225; border-bottom: 1px solid #eeeeee; }
        h3 { font-size: 1.5em; }
        h4 { font-size: 1.25em; }
        h5 { font-size: 1em; }
        h6 { font-size: 1em; color: #333333; margin-top: 0; margin-bottom: 0.8em; }
        ul, ol { padding-left: 30px; }
        li { margin: 0.2em 0; }
        li > ul, li > ol { margin: 0; }
        blockquote {
          border-left: 4px solid #dfe2e5;
          padding: 0 15px;
          color: #777777;
        }
        table { border-collapse: collapse; border-spacing: 0; color: #333333; }
        thead { background-color: #f8f8f8; }
        tr { border: 1px solid #dfe2e5; }
        tr:nth-child(2n) { background-color: #f8f8f8; }
        th, td { border: 1px solid #dfe2e5; padding: 6px 13px; text-align: left; }
        th { font-weight: bold; }
        code, tt {
          font-family: "Cascadia Mono", Consolas, "Microsoft YaHei UI", monospace;
          font-size: 0.9em;
          border: 1px solid #e7eaed;
          background-color: #f3f4f4;
          border-radius: 3px;
          padding: 2px 4px;
        }
        pre {
          font-family: "Cascadia Mono", Consolas, "Microsoft YaHei UI", monospace;
          font-size: 0.9em;
          line-height: 1.45;
          border: 1px solid #e7eaed;
          background-color: #f8f8f8;
          border-radius: 3px;
          padding: 8px 10px;
          white-space: pre-wrap;
        }
        pre code { border: 0; background-color: transparent; padding: 0; }
        hr { height: 2px; margin: 16px 0; background-color: #e7e7e7; border: 0; }
        """;

    private const string DarkCss = """
        html, body {
          margin: 0;
          padding: 0;
          background-color: #2b2b2b;
          color: #e6edf3;
          font-family: "Segoe UI Variable", "Microsoft YaHei UI", "Open Sans", "Helvetica Neue", Arial, sans-serif;
          font-size: 15px;
          line-height: 1.6;
        }
        #write { margin: 0; padding: 4px 8px 24px 8px; }
        p, blockquote, ul, ol, dl, table, pre { margin: 0.8em 0; }
        p, li, ul, ol, dl { color: #e6edf3; font-weight: normal; }
        strong, b { color: #e6edf3; font-weight: bold; }
        em { color: #e6edf3; font-style: italic; }
        a { color: #58a6ff; text-decoration: none; }
        h1, h2, h3, h4, h5, h6 {
          color: #f0f6fc;
          font-weight: bold;
          line-height: 1.4;
          margin-top: 1em;
          margin-bottom: 1em;
        }
        h1 { font-size: 2.25em; line-height: 1.2; border-bottom: 1px solid #3d444d; }
        h2 { font-size: 1.75em; line-height: 1.225; border-bottom: 1px solid #3d444d; }
        h3 { font-size: 1.5em; }
        h4 { font-size: 1.25em; }
        h5 { font-size: 1em; }
        h6 { font-size: 1em; color: #e6edf3; margin-top: 0; margin-bottom: 0.8em; }
        ul, ol { padding-left: 30px; }
        li { margin: 0.2em 0; }
        li > ul, li > ol { margin: 0; }
        blockquote { border-left: 4px solid #3d444d; padding: 0 15px; color: #9198a1; }
        table { border-collapse: collapse; border-spacing: 0; color: #e6edf3; }
        thead { background-color: #252525; }
        tr { border: 1px solid #3d444d; }
        tr:nth-child(2n) { background-color: #252525; }
        th, td { border: 1px solid #3d444d; padding: 6px 13px; text-align: left; }
        th { font-weight: bold; }
        code, tt {
          font-family: "Cascadia Mono", Consolas, "Microsoft YaHei UI", monospace;
          font-size: 0.9em;
          border: 1px solid #3d444d;
          background-color: #202020;
          color: #ff7b72;
          border-radius: 3px;
          padding: 2px 4px;
        }
        pre {
          font-family: "Cascadia Mono", Consolas, "Microsoft YaHei UI", monospace;
          font-size: 0.9em;
          line-height: 1.45;
          border: 1px solid #3d444d;
          background-color: #202020;
          border-radius: 3px;
          padding: 8px 10px;
          white-space: pre-wrap;
        }
        pre code { border: 0; background-color: transparent; color: #e6edf3; padding: 0; }
        hr { height: 2px; margin: 16px 0; background-color: #3d444d; border: 0; }
        """;

    private const string PreviewCss = """
        html, body { font-size: 14px; line-height: 1.45; }
        #write { padding: 0; }
        p, blockquote, ul, ol, dl, table, pre { margin: 0.35em 0; }
        h1, h2, h3, h4, h5, h6 { margin-top: 0.4em; margin-bottom: 0.4em; }
        ul, ol { padding-left: 24px; }
        th, td { padding: 4px 10px; }
        """;
}
