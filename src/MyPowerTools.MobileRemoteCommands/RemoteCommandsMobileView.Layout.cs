using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace MyPowerTools.MobileRemoteCommands;

/// <summary>
/// Code-built, single-column phone layout for the Remote Commands module surface.
///
/// The page is deliberately free of XAML: the Android CoreCLR host loads this assembly dynamically from
/// the tool package, so keeping the surface to plain controls removes an Avalonia resource pipeline from
/// the load path. Colours come from <see cref="RemoteCommandsMobilePalette"/>, which follows the Shell
/// theme passed in <c>MptAvaloniaSurfaceContext.Theme</c>, so the page matches the rest of MPT on both
/// light and dark phones.
/// </summary>
internal sealed partial class RemoteCommandsMobileView
{
    private const double TouchTarget = 44;

    private Control BuildLayout()
    {
        CreateControls();

        var root = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(14, 12, 14, 24),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        root.Children.Add(BuildHeaderCard());
        root.Children.Add(BuildRunCard());
        root.Children.Add(BuildCatalogEditorCard());
        root.Children.Add(BuildSettingsCard());
        root.Children.Add(BuildHostsCard());
        root.Children.Add(BuildHostKeysCard());
        root.Children.Add(BuildHistoryCard());
        root.Children.Add(BuildDiagnosticsCard());

        return new ScrollViewer
        {
            Content = root,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    /// <summary>
    /// Builds every brush-bearing control here, after the palette was selected from the Shell theme.
    /// </summary>
    private void CreateControls()
    {
        _pageTitle = new TextBlock { FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Brush("Text"), TextWrapping = TextWrapping.Wrap };
        _statusPill = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brush("Accent"), TextWrapping = TextWrapping.Wrap };
        _statusDetail = new TextBlock { FontSize = 12, Foreground = Brush("Muted"), TextWrapping = TextWrapping.Wrap };
        _transportWarning = new TextBlock { FontSize = 12, Foreground = Brush("Error"), TextWrapping = TextWrapping.Wrap };
        _backgroundHint = new TextBlock { FontSize = 12, Foreground = Brush("Muted"), TextWrapping = TextWrapping.Wrap };
        _feedbackText = new TextBlock { FontSize = 12, Foreground = Brush("Muted"), TextWrapping = TextWrapping.Wrap };

        _refreshButton = PrimaryButton("刷新状态");
        _cancelButton = SecondaryButton("取消执行");
        _commandPicker = new ComboBox { MinHeight = TouchTarget, HorizontalAlignment = HorizontalAlignment.Stretch };
        _commandDetail = Hint("");
        _hostPicker = new ComboBox { MinHeight = TouchTarget, HorizontalAlignment = HorizontalAlignment.Stretch };
        _missingAliases = Hint("");
        _input1Box = Field("粘贴或输入第一个输入");
        _input1Box.AcceptsReturn = true;
        _input1Box.MinHeight = 96;
        _input1Box.TextWrapping = TextWrapping.Wrap;
        _secondInputToggle = new CheckBox { Content = "使用第二个输入", MinHeight = TouchTarget };
        _input2Label = FieldLabel("输入 2");
        _input2Box = Field("粘贴或输入第二个输入");
        _input2Box.AcceptsReturn = true;
        _input2Box.MinHeight = 80;
        _input2Box.TextWrapping = TextWrapping.Wrap;
        _runButton = PrimaryButton("运行");
        _runCancelButton = SecondaryButton("取消");
        _runState = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brush("Text"), TextWrapping = TextWrapping.Wrap };
        _runEndpoint = new TextBlock { FontSize = 12, Foreground = Brush("Muted"), TextWrapping = TextWrapping.Wrap };
        _runMessage = new TextBlock { FontSize = 12, Foreground = Brush("Error"), TextWrapping = TextWrapping.Wrap };
        _outputViewer = new SelectableTextBlock
        {
            FontFamily = MonoFont,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("Text")
        };
        _copyOutputButton = SecondaryButton("复制输出");

        _pendingEndpoint = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Brush("Warning"), TextWrapping = TextWrapping.Wrap };
        _pendingReason = new TextBlock { FontSize = 12, Foreground = Brush("Text"), TextWrapping = TextWrapping.Wrap };
        _pendingDetail = new SelectableTextBlock { FontFamily = MonoFont, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brush("Text") };
        _pendingMessage = new TextBlock { FontSize = 12, Foreground = Brush("Muted"), TextWrapping = TextWrapping.Wrap };
        _trustAcknowledged = new CheckBox { Content = "我已核对上面的 SHA256 指纹", MinHeight = TouchTarget };
        _trustButton = PrimaryButton("信任此指纹并重新运行");
        _trustDismissButton = SecondaryButton("取消");

        _hostList = new ItemsControl { ItemsSource = _hostRows };
        _removalPrompt = new TextBlock { FontSize = 12, Foreground = Brush("Warning"), TextWrapping = TextWrapping.Wrap };
        _refreshAliasesButton = SecondaryButton("重新读取别名");
        _missingAliasList = new ItemsControl { ItemsSource = _missingAliasRows };
        _aliasField = Field("例如 r743");
        _hostField = Field("例如 100.64.0.2 或 host.example.com");
        _portField = Field("22");
        _usernameField = Field("登录用户名");
        _passwordAuth = new RadioButton { Content = "密码", GroupName = "rc-auth", MinHeight = TouchTarget, IsChecked = true };
        _keyAuth = new RadioButton { Content = "私钥", GroupName = "rc-auth", MinHeight = TouchTarget };
        _authHint = Hint("");
        _passwordField = Field("登录密码（仅写入系统凭据库）");
        _passwordField.PasswordChar = '●';
        _privateKeyField = Field("-----BEGIN OPENSSH PRIVATE KEY-----");
        _privateKeyField.AcceptsReturn = true;
        _privateKeyField.MinHeight = 120;
        _privateKeyField.TextWrapping = TextWrapping.NoWrap;
        _privateKeyField.FontFamily = MonoFont;
        _privateKeyField.FontSize = 11;
        _passphraseField = Field("私钥口令（可留空）");
        _passphraseField.PasswordChar = '●';
        _saveHostButton = PrimaryButton("保存映射");
        _clearFormButton = SecondaryButton("清空表单");
        _formMessage = new TextBlock { FontSize = 12, Foreground = Brush("Muted"), TextWrapping = TextWrapping.Wrap };

        _trustedKeyList = new ItemsControl { ItemsSource = _trustedKeyRows };
        _trustedKeysEmpty = Hint("还没有确认过任何主机密钥。");

        _historyText = new TextBlock { FontSize = 12, Foreground = Brush("Muted"), TextWrapping = TextWrapping.Wrap };
        _clearHistoryButton = SecondaryButton("清空执行历史");

        _commandsPath = new TextBlock { FontSize = 11, FontFamily = MonoFont, Foreground = Brush("Muted"), TextWrapping = TextWrapping.Wrap };
        _commandsFileStatus = new TextBlock { FontSize = 12, Foreground = Brush("Text"), TextWrapping = TextWrapping.Wrap };
        _catalogError = new TextBlock { FontSize = 12, Foreground = Brush("Error"), TextWrapping = TextWrapping.Wrap };
        _settingsSummary = new TextBlock { FontSize = 12, Foreground = Brush("Muted"), TextWrapping = TextWrapping.Wrap };
        _dataDirectory = new TextBlock { FontSize = 11, FontFamily = MonoFont, Foreground = Brush("Muted"), TextWrapping = TextWrapping.Wrap };
        _revalidateButton = SecondaryButton("重新校验 commands.yaml");

        _catalogToggleButton = SecondaryButton("展开命令配置（编辑 commands.yaml）");
        _catalogEditor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = MonoFont,
            FontSize = 11,
            MinHeight = 240,
            Watermark = "id: ...\nlabel: ...\ncommand: ...\ntype: shell",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _catalogEditor.Bind(TextBox.TextProperty, new Binding(nameof(RemoteCommandsMobileViewModel.CatalogYaml))
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        ScrollViewer.SetHorizontalScrollBarVisibility(_catalogEditor, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_catalogEditor, ScrollBarVisibility.Auto);
        _catalogDirtyText = new TextBlock { FontSize = 12, Foreground = Brush("Muted"), TextWrapping = TextWrapping.Wrap };
        _catalogYamlStatus = Hint("");
        _catalogSaveMessage = new TextBlock { FontSize = 12, Foreground = Brush("Warning"), TextWrapping = TextWrapping.Wrap };
        _catalogSaveButton = PrimaryButton("保存到模块");
        _catalogImportButton = SecondaryButton("从剪贴板导入");
        _catalogReloadButton = SecondaryButton("重新载入");
        _catalogCopyButton = SecondaryButton("复制当前 YAML");

        _settingsDefaultHostField = Field("默认主机别名，例如 r743");
        _settingsKnownHostsField = Field("每行一个别名");
        _settingsKnownHostsField.AcceptsReturn = true;
        _settingsKnownHostsField.MinHeight = 84;
        _settingsKnownHostsField.TextWrapping = TextWrapping.Wrap;
        _settingsRetentionField = Field("10-5000");
        _settingsCondaField = Field("/home/user/miniconda3/bin/conda");
        _settingsCondaField.FontFamily = MonoFont;
        _settingsCondaField.FontSize = 12;
        _settingsTimeoutField = Field("1-1440");
        _settingsDirtyText = new TextBlock { FontSize = 12, Foreground = Brush("Muted"), TextWrapping = TextWrapping.Wrap };
        _settingsMessage = new TextBlock { FontSize = 12, Foreground = Brush("Muted"), TextWrapping = TextWrapping.Wrap };
        _modulePreferenceHint = Hint("");
        _settingsSaveButton = PrimaryButton("保存设置");
        _settingsResetButton = SecondaryButton("还原为模块状态");
    }

    // ---------------------------------------------------------------- cards

    private Border BuildHeaderCard()
    {
        _refreshButton.Click += async (_, _) => await _viewModel.RefreshAsync();
        _cancelButton.Click += async (_, _) => await _viewModel.CancelAsync();

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(_pageTitle);
        content.Children.Add(_statusPill);
        content.Children.Add(_statusDetail);
        content.Children.Add(_transportWarning);
        content.Children.Add(_backgroundHint);
        content.Children.Add(_feedbackText);
        content.Children.Add(new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _refreshButton, _cancelButton }
        });
        return Card(content);
    }

    private Border BuildRunCard()
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(SectionTitle("运行命令"));

        _commandPicker.SelectionChanged += (_, _) =>
        {
            if (_syncing || _commandPicker.SelectedItem is not MobileCommandDefinition command)
            {
                return;
            }

            _viewModel.SelectedCommand = command;
        };
        content.Children.Add(FieldLabel("命令（来自共享 commands.yaml）"));
        content.Children.Add(_commandPicker);
        content.Children.Add(_commandDetail);

        _hostPicker.SelectionChanged += (_, _) =>
        {
            if (_syncing || _hostPicker.SelectedItem is not MobileHostChoice choice)
            {
                return;
            }

            _viewModel.SelectedHost = choice.Alias;
        };
        content.Children.Add(FieldLabel("主机"));
        content.Children.Add(_hostPicker);
        content.Children.Add(_missingAliases);

        content.Children.Add(FieldLabel("输入 1"));
        content.Children.Add(_input1Box);
        content.Children.Add(_secondInputToggle);
        content.Children.Add(_input2Label);
        content.Children.Add(_input2Box);

        _runButton.Click += async (_, _) => await _viewModel.RunAsync();
        _runCancelButton.Click += async (_, _) => await _viewModel.CancelAsync();
        content.Children.Add(new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _runButton, _runCancelButton }
        });

        content.Children.Add(_runState);
        content.Children.Add(_runEndpoint);
        content.Children.Add(_runMessage);
        content.Children.Add(BuildPendingHostKeyCard());
        content.Children.Add(BuildOutputCard());
        return Card(content);
    }

    private Border BuildPendingHostKeyCard()
    {
        _trustAcknowledged.Click += (_, _) =>
            _viewModel.TrustFingerprintAcknowledged = _trustAcknowledged.IsChecked == true;
        _trustButton.Click += async (_, _) => await _viewModel.TrustPendingHostKeyAndRunAsync();
        _trustDismissButton.Click += (_, _) => _viewModel.DismissPendingHostKey();

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(SectionTitle("确认主机密钥"));
        content.Children.Add(_pendingEndpoint);
        content.Children.Add(_pendingReason);
        content.Children.Add(_pendingDetail);
        content.Children.Add(_pendingMessage);
        content.Children.Add(_trustAcknowledged);
        content.Children.Add(new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _trustButton, _trustDismissButton }
        });
        _pendingCard = Border(content, "WarningSurface", "Warning");
        return _pendingCard;
    }

    private Border BuildOutputCard()
    {
        _copyOutputButton.Click += async (_, _) => await CopyOutputAsync();

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(SectionTitle("输出"));
        Grid.SetColumn(_copyOutputButton, 1);
        header.Children.Add(_copyOutputButton);

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(header);
        content.Children.Add(new Border
        {
            Background = Brush("Inset"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Height = 240,
            Child = new ScrollViewer
            {
                Content = _outputViewer,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }
        });
        return Card(content);
    }

    private Border BuildHostsCard()
    {
        _refreshAliasesButton.Click += async (_, _) => await _viewModel.RefreshAsync();

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(SectionTitle("主机映射"));
        content.Children.Add(Hint("手机上没有 ~/.ssh/config：别名必须显式映射到真实地址，凭据只存系统凭据库。"));
        content.Children.Add(_hostList);
        content.Children.Add(_removalPrompt);
        content.Children.Add(_refreshAliasesButton);
        content.Children.Add(_missingAliasList);

        content.Children.Add(SectionTitle("添加或更新映射"));
        content.Children.Add(FieldLabel("别名"));
        content.Children.Add(_aliasField);
        content.Children.Add(FieldLabel("真实主机"));
        content.Children.Add(_hostField);
        content.Children.Add(FieldLabel("端口"));
        content.Children.Add(_portField);
        content.Children.Add(FieldLabel("用户名"));
        content.Children.Add(_usernameField);
        content.Children.Add(FieldLabel("认证方式"));
        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Children = { _passwordAuth, _keyAuth }
        });
        content.Children.Add(_authHint);
        content.Children.Add(_passwordField);
        content.Children.Add(_privateKeyField);
        content.Children.Add(_passphraseField);

        _saveHostButton.Click += async (_, _) => await _viewModel.SaveHostAsync();
        _clearFormButton.Click += (_, _) => _viewModel.ClearHostForm();
        content.Children.Add(new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _saveHostButton, _clearFormButton }
        });
        content.Children.Add(_formMessage);
        return Card(content);
    }

    private Border BuildHostKeysCard()
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(SectionTitle("已确认的主机密钥"));
        content.Children.Add(Hint("MPT 从不自动信任任何主机：每个指纹都由你单独确认，换钥会重新询问。"));
        content.Children.Add(_trustedKeyList);
        content.Children.Add(_trustedKeysEmpty);
        return Card(content);
    }

    private Border BuildCatalogEditorCard()
    {
        _catalogToggleButton.Click += (_, _) =>
            _viewModel.CatalogEditorExpanded = !_viewModel.CatalogEditorExpanded;
        _catalogSaveButton.Click += async (_, _) => await _viewModel.SaveCatalogAsync();
        _catalogImportButton.Click += async (_, _) => await ImportCatalogFromClipboardAsync();
        _catalogCopyButton.Click += async (_, _) => await CopyTextAsync(_viewModel.CatalogYaml, "命令配置");
        _catalogReloadButton.Click += async (_, _) => await _viewModel.ReloadCatalogYamlAsync();

        _catalogEditorBody = new StackPanel { Spacing = 8, IsVisible = false };
        _catalogEditorBody.Children.Add(_catalogEditor);
        _catalogEditorBody.Children.Add(_catalogDirtyText);
        _catalogEditorBody.Children.Add(_catalogYamlStatus);
        _catalogEditorBody.Children.Add(new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _catalogSaveButton, _catalogImportButton, _catalogReloadButton, _catalogCopyButton }
        });
        _catalogEditorBody.Children.Add(_catalogSaveMessage);
        _catalogEditorBody.Children.Add(Hint(
            "保存由模块用同一份解析器校验后写入唯一正式的 commands.yaml；校验不通过时这里会显示原因，编辑内容不会被丢弃。"));

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(SectionTitle("命令配置"));
        content.Children.Add(Hint("手机可以直接维护共享的 commands.yaml：改完点保存，模块校验通过后命令目录立即刷新。"));
        content.Children.Add(_catalogToggleButton);
        content.Children.Add(_catalogEditorBody);
        _catalogEditorCard = Card(content);
        return _catalogEditorCard;
    }

    private Border BuildSettingsCard()
    {
        _settingsSaveButton.Click += async (_, _) => await SaveSettingsFromControlsAsync();
        _settingsResetButton.Click += (_, _) => _viewModel.DiscardSettingsEdits();

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(SectionTitle("设置（可直接编辑）"));
        content.Children.Add(FieldLabel("默认主机"));
        content.Children.Add(_settingsDefaultHostField);
        content.Children.Add(FieldLabel("主机别名列表（每行一个）"));
        content.Children.Add(_settingsKnownHostsField);
        content.Children.Add(FieldLabel("历史保留条数"));
        content.Children.Add(_settingsRetentionField);
        content.Children.Add(FieldLabel("CONDA_EXE（远端前缀）"));
        content.Children.Add(_settingsCondaField);
        content.Children.Add(FieldLabel("单次执行超时（分钟）"));
        content.Children.Add(_settingsTimeoutField);
        content.Children.Add(new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _settingsSaveButton, _settingsResetButton }
        });
        content.Children.Add(_settingsDirtyText);
        content.Children.Add(_modulePreferenceHint);
        content.Children.Add(_settingsMessage);
        return Card(content);
    }

    private Border BuildHistoryCard()
    {
        _clearHistoryButton.Click += async (_, _) => await OnClearHistoryClickedAsync();

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(SectionTitle("执行历史"));
        content.Children.Add(_historyText);
        content.Children.Add(_clearHistoryButton);
        return Card(content);
    }

    private async Task OnClearHistoryClickedAsync()
    {
        if (_viewModel.HistoryClearPending)
        {
            await _viewModel.ConfirmHistoryClearAsync();
            return;
        }

        _viewModel.RequestHistoryClear();
    }

    private Border BuildDiagnosticsCard()
    {
        _revalidateButton.Click += (_, _) => _viewModel.RefreshCommandsFileStatus();

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(SectionTitle("命令文件与共享设置"));
        content.Children.Add(_commandsPath);
        content.Children.Add(_commandsFileStatus);
        content.Children.Add(_catalogError);
        content.Children.Add(_settingsSummary);
        content.Children.Add(_dataDirectory);
        content.Children.Add(_revalidateButton);
        content.Children.Add(Hint("上面“设置”卡片里的改动会写入模块读取的同一份文件；这里显示的是模块当前生效的状态。"));
        return Card(content);
    }

    // ---------------------------------------------------------------- primitives

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, monospace");

    private static IBrush Brush(string role) => RemoteCommandsMobilePalette.Current.Brush(role);

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeight.SemiBold,
        Foreground = Brush("Text")
    };

    private static TextBlock FieldLabel(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeight.Medium,
        Foreground = Brush("Muted"),
        Margin = new Thickness(0, 4, 0, 0)
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("Muted")
    };

    private static TextBox Field(string watermark) => new()
    {
        Watermark = watermark,
        MinHeight = TouchTarget,
        FontSize = 14
    };

    private static Button PrimaryButton(string text) => new()
    {
        Content = text,
        MinHeight = TouchTarget,
        Padding = new Thickness(16, 8),
        Margin = new Thickness(0, 0, 8, 0),
        Background = Brush("Accent"),
        Foreground = Brush("AccentText"),
        HorizontalContentAlignment = HorizontalAlignment.Center
    };

    private static Button SecondaryButton(string text) => new()
    {
        Content = text,
        MinHeight = TouchTarget,
        Padding = new Thickness(14, 8),
        Margin = new Thickness(0, 0, 8, 0),
        HorizontalContentAlignment = HorizontalAlignment.Center
    };

    private static Border Card(Control child) => Border(child, "Card", "Border");

    private static Border Border(Control child, string backgroundRole, string borderRole) => new()
    {
        Background = Brush(backgroundRole),
        BorderBrush = Brush(borderRole),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(14),
        Child = child
    };
}

/// <summary>Small palette that follows the Shell theme so the phone page does not fight the host.</summary>
internal sealed record RemoteCommandsMobilePalette(bool Dark)
{
    public static RemoteCommandsMobilePalette ForTheme(string? theme) =>
        new(string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase));

    public static RemoteCommandsMobilePalette Current { get; set; } = new(false);

    public IBrush Brush(string role) => new SolidColorBrush(Color.Parse(ColorFor(role)));

    private string ColorFor(string role) => (Dark, role) switch
    {
        (false, "Page") => "#F7F8FA",
        (false, "Card") => "#FFFFFF",
        (false, "Inset") => "#F3F4F6",
        (false, "Border") => "#D8DEE7",
        (false, "Text") => "#111827",
        (false, "Muted") => "#6B7280",
        (false, "Accent") => "#2563EB",
        (false, "AccentText") => "#FFFFFF",
        (false, "Success") => "#15803D",
        (false, "Warning") => "#B45309",
        (false, "WarningSurface") => "#FEF3C7",
        (false, "Error") => "#B91C1C",
        (false, "ErrorSurface") => "#FEE2E2",
        (true, "Page") => "#111827",
        (true, "Card") => "#1F2937",
        (true, "Inset") => "#111827",
        (true, "Border") => "#374151",
        (true, "Text") => "#F9FAFB",
        (true, "Muted") => "#9CA3AF",
        (true, "Accent") => "#60A5FA",
        (true, "AccentText") => "#0B1220",
        (true, "Success") => "#4ADE80",
        (true, "Warning") => "#FCD34D",
        (true, "WarningSurface") => "#78350F",
        (true, "Error") => "#FCA5A5",
        (true, "ErrorSurface") => "#7F1D1D",
        _ => "#00000000"
    };
}
