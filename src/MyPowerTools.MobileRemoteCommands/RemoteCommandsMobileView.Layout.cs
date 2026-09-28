using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace MyPowerTools.MobileRemoteCommands;

/// <summary>
/// Code-built, single-column phone layout for the Remote Commands module surface.
///
/// The page follows the approved prototype: the real <c>commands.yaml</c> catalog is the primary list,
/// each job gets one bottom sheet (run, edit a command, manage the connection, edit the raw catalog),
/// and progress/cancel/retry live with the run itself. The page is deliberately free of XAML: the
/// Android CoreCLR host loads this assembly dynamically from the tool package, so keeping the surface to
/// plain controls removes an Avalonia resource pipeline from the load path.
///
/// Every control carries the shared mobile theme classes (<see cref="RemoteCommandsMobileTheme"/>) and
/// sets no local colour on them, so the SDK theme owns colours, typography and touch metrics. Marks are
/// vector <see cref="Path"/> icons from the prototype rather than glyph characters, so they render the
/// same on every device without an icon font.
/// </summary>
internal sealed partial class RemoteCommandsMobileView
{
    private const double TouchTarget = 44;

    private bool _dark;

    private Control BuildLayout()
    {
        _dark = RemoteCommandsMobilePalette.ForTheme(_context.Theme).Dark;
        RemoteCommandsMobilePalette.Current = RemoteCommandsMobilePalette.ForTheme(_context.Theme);
        if (RemoteCommandsMobileTheme.CreateFallbackStyles(_dark) is { } fallback)
        {
            foreach (var style in fallback)
            {
                Styles.Add(style);
            }
        }

        CreateControls();

        _pagePanel = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Stretch };
        _pagePanel.Classes.Add(RemoteCommandsMobileTheme.PageClass);
        _pagePanel.Children.Add(BuildSubnav());
        _pagePanel.Children.Add(_pageTitle);
        _pagePanel.Children.Add(_pageSubtitle);
        _pagePanel.Children.Add(BuildStatusLine());
        _pagePanel.Children.Add(_transportWarning);
        _pagePanel.Children.Add(_feedbackText);
        _pagePanel.Children.Add(BuildRunningCard());
        _pagePanel.Children.Add(BuildCommandListSection());
        _pagePanel.Children.Add(BuildLastResultSection());
        _pagePanel.Children.Add(BuildFooter());

        var scroller = new ScrollViewer
        {
            Content = _pagePanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var root = new Grid();
        root.Classes.Add(RemoteCommandsMobileTheme.RootClass);
        root.Children.Add(scroller);
        root.Children.Add(BuildOverlay());

        // 320 dp phones get the narrower page margin from the design table.
        root.SizeChanged += (_, _) => ApplyPageMargin(root.Bounds.Width);

        // Android's back key is routed by the host, but a hardware/desktop Escape key and the headless
        // tests use the same page-level handler.
        root.AddHandler(KeyDownEvent, OnRootKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        return root;
    }

    /// <summary>
    /// Page margins come from the theme's page tokens; the page only selects the narrow variant on a
    /// 320 dp screen, so a theme-side font scale or padding change still applies.
    /// </summary>
    private void ApplyPageMargin(double width)
    {
        var narrow = width > 0 && width < 340;
        ToggleClass(_pagePanel, RemoteCommandsMobileTheme.PageNarrowClass, narrow);
    }

    private Control BuildSubnav()
    {
        _backButton = BackButton();
        _backButton.Content = Icon(MobileIcons.Back);
        ToolTip.SetTip(_backButton, "返回工具库");
        _backButton.Click += async (_, _) => await _context.NavigateAsync("", "", null);

        _refreshButton = IconButton();
        _refreshButton.Content = Icon(MobileIcons.Refresh);
        ToolTip.SetTip(_refreshButton, "重新读取模块状态");
        _refreshButton.Click += async (_, _) => await _viewModel.RefreshAsync();

        var label = Text("远程命令", RemoteCommandsMobileTheme.MetaClass);
        label.VerticalAlignment = VerticalAlignment.Center;

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        row.Children.Add(_backButton);
        Grid.SetColumn(label, 1);
        label.Margin = new Thickness(10, 0, 0, 0);
        row.Children.Add(label);
        Grid.SetColumn(_refreshButton, 2);
        row.Children.Add(_refreshButton);
        return row;
    }

    private Control BuildStatusLine()
    {
        _statusPillText = Text("", RemoteCommandsMobileTheme.PillTextClass);
        _statusPill = new Border { Child = _statusPillText };
        _statusPill.Classes.Add(RemoteCommandsMobileTheme.PillClass);

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(_statusPill);
        panel.Children.Add(_statusDetail);
        return panel;
    }

    private Control BuildRunningCard()
    {
        _runningCancelButton.Click += async (_, _) => await _viewModel.CancelAsync();

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(_runningState);
        Grid.SetColumn(_runningCancelButton, 1);
        header.Children.Add(_runningCancelButton);

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(header);
        content.Children.Add(_runningEndpoint);
        content.Children.Add(_runningProgress);
        _runningCard = Card(content);
        _runningCard.IsVisible = false;
        return _runningCard;
    }

    private Control BuildCommandListSection()
    {
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(SectionTitle("常用指令"));
        Grid.SetColumn(_addCommandButton, 1);
        heading.Children.Add(_addCommandButton);

        _searchBox = new Border { Child = _searchField, IsVisible = false };
        _searchBox.Classes.Add(RemoteCommandsMobileTheme.SearchBoxClass);

        _commandList = new ItemsControl { ItemsSource = _commandRows };
        var listCard = new Border { Child = _commandList };
        listCard.Classes.Add(RemoteCommandsMobileTheme.ListCardClass);
        listCard.Margin = new Thickness(0, 8, 0, 0);

        var addFirst = SecondaryButton("添加第一条命令");
        addFirst.HorizontalAlignment = HorizontalAlignment.Left;
        addFirst.Margin = new Thickness(0, 10, 0, 0);
        addFirst.Click += (_, _) => _viewModel.OpenCommandForm(null);
        var emptyBody = new StackPanel { Spacing = 4 };
        emptyBody.Children.Add(Text("还没有命令", RemoteCommandsMobileTheme.EmptyTitleClass));
        emptyBody.Children.Add(_emptyCommandsText);
        emptyBody.Children.Add(addFirst);
        _emptyCommandsCard = Card(emptyBody);
        _emptyCommandsCard.Margin = new Thickness(0, 8, 0, 0);

        var section = new StackPanel { Spacing = 0 };
        section.Children.Add(heading);
        section.Children.Add(_commandListCaption);
        section.Children.Add(_searchBox);
        section.Children.Add(listCard);
        section.Children.Add(_emptyCommandsCard);
        section.Children.Add(_noMatchesText);
        return section;
    }

    private Control BuildLastResultSection()
    {
        _lastResultCard.PointerPressed += (_, _) => _viewModel.OpenLastResultSheet();

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(SectionTitle("上次结果"));
        Grid.SetColumn(_lastResultMeta, 1);
        header.Children.Add(_lastResultMeta);

        var stateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        stateRow.Children.Add(_lastResultGlyph);
        stateRow.Children.Add(_lastResultState);

        var output = new Border { Child = _lastResultPreview, Margin = new Thickness(0, 4, 0, 0) };
        output.Classes.Add(RemoteCommandsMobileTheme.CommandOutputClass);

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(stateRow);
        body.Children.Add(_lastResultDetail);
        body.Children.Add(output);

        var empty = new StackPanel { Spacing = 6 };
        empty.Children.Add(_lastResultEmptyGlyph);
        empty.Children.Add(_lastResultEmpty);

        _lastResultBody = body;
        _lastResultEmptyPanel = empty;
        _lastResultCard.Child = new StackPanel { Spacing = 8, Children = { body, empty } };

        var section = new StackPanel { Spacing = 0 };
        section.Children.Add(header);
        section.Children.Add(_lastResultCard);
        _lastResultCard.Margin = new Thickness(0, 8, 0, 0);
        return section;
    }

    private Control BuildFooter()
    {
        _connectionButton = QuietButton("管理服务器连接");
        _connectionButton.Click += (_, _) => _viewModel.OpenConnectionSheet();
        _catalogButton = QuietButton("命令配置（YAML）");
        _catalogButton.Click += (_, _) => _viewModel.OpenCatalogSheet();

        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(_connectionButton);
        panel.Children.Add(_catalogButton);
        return panel;
    }

    // ---------------------------------------------------------------- overlay

    private Control BuildOverlay()
    {
        _scrim = new Border();
        _scrim.Classes.Add(RemoteCommandsMobileTheme.OverlayClass);
        _scrim.PointerPressed += (_, args) =>
        {
            _viewModel.CloseSheet();
            args.Handled = true;
        };

        _sheetBody = new StackPanel { Spacing = 12 };
        _sheetBody.Children.Add(BuildSheetHeader());
        _sheetBody.Children.Add(BuildRunPanel());
        _sheetBody.Children.Add(BuildCommandPanel());
        _sheetBody.Children.Add(BuildConnectionPanel());
        _sheetBody.Children.Add(BuildCatalogPanel());

        var grabber = new Border();
        grabber.Classes.Add(RemoteCommandsMobileTheme.SheetGrabberClass);

        var sheetContent = new StackPanel { Spacing = 0 };
        sheetContent.Children.Add(grabber);
        sheetContent.Children.Add(new ScrollViewer
        {
            Content = _sheetBody,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });

        _sheet = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = sheetContent
        };
        _sheet.Classes.Add(RemoteCommandsMobileTheme.SheetClass);

        _overlay = new Grid { IsVisible = false };
        _overlay.Children.Add(_scrim);
        _overlay.Children.Add(_sheet);
        _overlay.SizeChanged += (_, _) =>
            _sheet.MaxHeight = Math.Max(320, _overlay.Bounds.Height * 0.92);
        return _overlay;
    }

    private Control BuildSheetHeader()
    {
        _sheetCloseButton = CloseButton();
        _sheetCloseButton.Content = Icon(MobileIcons.Close);
        ToolTip.SetTip(_sheetCloseButton, "关闭");
        _sheetCloseButton.Click += (_, _) => _viewModel.CloseSheet();

        var copy = new StackPanel { Spacing = 4 };
        copy.Children.Add(_sheetTitle);
        copy.Children.Add(_sheetSubtitle);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(copy);
        Grid.SetColumn(_sheetCloseButton, 1);
        _sheetCloseButton.VerticalAlignment = VerticalAlignment.Top;
        _sheetCloseButton.Margin = new Thickness(10, 0, 0, 0);
        grid.Children.Add(_sheetCloseButton);
        return grid;
    }

    // ---------------------------------------------------------------- run sheet

    private Control BuildRunPanel()
    {
        _runButton.Click += async (_, _) => await RunFromControlsAsync();
        _cancelRunButton.Click += async (_, _) => await _viewModel.CancelAsync();
        _retryButton.Click += async (_, _) => await _viewModel.RetryAsync();
        _editCommandButton.Click += (_, _) => _viewModel.EditSelectedCommand();
        _runDoneButton.Click += (_, _) => _viewModel.CloseSheet();
        _copyOutputButton.Click += async (_, _) => await CopyOutputAsync();
        _trustButton.Click += async (_, _) => await _viewModel.TrustPendingHostKeyAndRunAsync();
        _trustDismissButton.Click += (_, _) => _viewModel.DismissPendingHostKey();
        _hostPicker.SelectionChanged += (_, _) =>
        {
            if (_syncing || _hostPicker.SelectedItem is not MobileHostChoice choice)
            {
                return;
            }

            _viewModel.SelectedHost = choice.Alias;
        };

        var panel = _runPanel;
        panel.Spacing = 10;
        panel.Children.Add(_runCommandDetail);
        panel.Children.Add(FieldLabel("主机"));
        panel.Children.Add(_hostPicker);
        panel.Children.Add(_input1Label);
        panel.Children.Add(_input1Box);
        panel.Children.Add(_secondInputToggle);
        panel.Children.Add(_input2Label);
        panel.Children.Add(_input2Box);
        panel.Children.Add(_runInputCaption);

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        buttons.Children.Add(_runButton);
        Grid.SetColumn(_cancelRunButton, 1);
        _cancelRunButton.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(_cancelRunButton);
        panel.Children.Add(buttons);

        var progress = new StackPanel { Spacing = 8 };
        progress.Children.Add(_runProgress);
        progress.Children.Add(_stageList);
        panel.Children.Add(progress);

        var stateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        stateRow.Children.Add(_runStateGlyph);
        stateRow.Children.Add(_runState);
        panel.Children.Add(stateRow);
        panel.Children.Add(_runEndpoint);
        panel.Children.Add(_runMessage);
        panel.Children.Add(BuildPermissionNotice());
        panel.Children.Add(BuildPendingHostKeyCard());

        // The command line and its transcript share one command-output surface, which is what gives the
        // mono header the contrast the theme designed for it.
        var outputBody = new StackPanel { Spacing = 6 };
        outputBody.Children.Add(_outputHeader);
        outputBody.Children.Add(new ScrollViewer
        {
            Content = _outputViewer,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 260
        });
        var output = new Border { MinHeight = 140, Child = outputBody };
        output.Classes.Add(RemoteCommandsMobileTheme.CommandOutputClass);
        panel.Children.Add(output);
        panel.Children.Add(_editCommandButton);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        footer.Children.Add(_retryButton);
        Grid.SetColumn(_copyOutputButton, 1);
        _copyOutputButton.Margin = new Thickness(8, 0, 0, 0);
        footer.Children.Add(_copyOutputButton);
        Grid.SetColumn(_runDoneButton, 2);
        _runDoneButton.Margin = new Thickness(8, 0, 0, 0);
        footer.Children.Add(_runDoneButton);
        panel.Children.Add(footer);

        return panel;
    }

    private Control BuildPermissionNotice()
    {
        _permissionNotice = new Border { Child = _permissionText, IsVisible = false };
        _permissionNotice.Classes.Add(RemoteCommandsMobileTheme.NoticeClass);
        _permissionNotice.Classes.Add(RemoteCommandsMobileTheme.WarningClass);
        return _permissionNotice;
    }

    private Border BuildPendingHostKeyCard()
    {
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

        _pendingCard = new Border { Child = content, IsVisible = false };
        _pendingCard.Classes.Add(RemoteCommandsMobileTheme.NoticeClass);
        _pendingCard.Classes.Add(RemoteCommandsMobileTheme.WarningClass);
        return _pendingCard;
    }

    // ---------------------------------------------------------------- command sheet

    private Control BuildCommandPanel()
    {
        _saveCommandButton.Click += async (_, _) => await SaveCommandFromControlsAsync();
        _cancelCommandButton.Click += (_, _) => _viewModel.CancelCommandForm();
        _formAdvancedToggle.Click += (_, _) => _viewModel.ToggleCommandFormAdvanced();
        _deleteCommandButton.Click += async (_, _) =>
        {
            if (_viewModel.CommandDeletePending)
            {
                await _viewModel.ConfirmCommandRemovalAsync();
                return;
            }

            _viewModel.RequestCommandRemoval();
        };

        var advanced = new StackPanel { Spacing = 10, IsVisible = false };
        advanced.Children.Add(FieldLabel("标识（历史与分享链接使用）"));
        advanced.Children.Add(_formIdField);
        advanced.Children.Add(FieldLabel("运行位置"));
        advanced.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Children = { _formShellType, _formLocalType }
        });
        advanced.Children.Add(FieldLabel("固定主机（可选）"));
        advanced.Children.Add(_formHostField);
        advanced.Children.Add(FieldLabel("输入 1 标签"));
        advanced.Children.Add(_formInput1LabelField);
        advanced.Children.Add(FieldLabel("输入 1 提示"));
        advanced.Children.Add(_formInput1PlaceholderField);
        advanced.Children.Add(_formSecondInputToggle);
        advanced.Children.Add(FieldLabel("输入 2 标签"));
        advanced.Children.Add(_formInput2LabelField);
        advanced.Children.Add(FieldLabel("输入 2 提示"));
        advanced.Children.Add(_formInput2PlaceholderField);
        _formAdvancedPanel = advanced;

        var panel = _commandPanel;
        panel.Spacing = 10;
        panel.Children.Add(FieldLabel("名称"));
        panel.Children.Add(_formLabelField);
        panel.Children.Add(FieldLabel("命令"));
        panel.Children.Add(_formCommandField);
        panel.Children.Add(FieldLabel("说明"));
        panel.Children.Add(_formDescriptionField);
        panel.Children.Add(_formTypeHint);
        panel.Children.Add(_formAdvancedToggle);
        panel.Children.Add(advanced);

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        buttons.Children.Add(_saveCommandButton);
        Grid.SetColumn(_cancelCommandButton, 1);
        _cancelCommandButton.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(_cancelCommandButton);
        panel.Children.Add(buttons);
        panel.Children.Add(_deleteCommandButton);
        panel.Children.Add(_commandFormMessage);
        panel.Children.Add(Text(
            "保存由模块用同一份解析器校验后写入唯一正式的 commands.yaml；校验不通过时这里会显示原因，表单内容不会被丢弃。",
            RemoteCommandsMobileTheme.NoteClass));

        return panel;
    }

    // ---------------------------------------------------------------- connection sheet

    private Control BuildConnectionPanel()
    {
        _saveHostButton.Click += async (_, _) => await _viewModel.SaveHostAsync();
        _clearFormButton.Click += (_, _) => _viewModel.ClearHostForm();
        _revalidateButton.Click += (_, _) => _viewModel.RefreshCommandsFileStatus();
        _openCatalogButton.Click += (_, _) => _viewModel.OpenCatalogSheet();
        _clearHistoryButton.Click += async (_, _) =>
        {
            if (_viewModel.HistoryClearPending)
            {
                await _viewModel.ConfirmHistoryClearAsync();
                return;
            }

            _viewModel.RequestHistoryClear();
        };

        var panel = _connectionPanel;
        panel.Spacing = 10;
        panel.Children.Add(SectionTitle("已保存的连接"));
        panel.Children.Add(_hostSummary);
        panel.Children.Add(_hostList);
        panel.Children.Add(_hostEmptyText);
        panel.Children.Add(_removalPrompt);
        panel.Children.Add(_missingAliasesText);
        panel.Children.Add(_missingAliasList);

        panel.Children.Add(SectionTitle("添加或更新映射"));
        panel.Children.Add(FieldLabel("别名（与命令里的主机名一致）"));
        panel.Children.Add(_aliasField);
        panel.Children.Add(FieldLabel("真实主机"));
        panel.Children.Add(_realHostField);
        panel.Children.Add(FieldLabel("端口"));
        panel.Children.Add(_portField);
        panel.Children.Add(FieldLabel("用户名"));
        panel.Children.Add(_usernameField);
        panel.Children.Add(FieldLabel("认证方式"));
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Children = { _passwordAuth, _keyAuth }
        });
        panel.Children.Add(_authHint);
        panel.Children.Add(_passwordField);
        panel.Children.Add(_privateKeyField);
        panel.Children.Add(_passphraseField);
        panel.Children.Add(new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _saveHostButton, _clearFormButton }
        });
        panel.Children.Add(_formMessage);

        panel.Children.Add(SectionTitle("已确认的主机密钥"));
        panel.Children.Add(Text(
            "MPT 从不自动信任任何主机：每个指纹都由你单独确认，换钥会重新询问。",
            RemoteCommandsMobileTheme.NoteClass));
        panel.Children.Add(_trustedKeyList);
        panel.Children.Add(_trustedKeysEmpty);
        panel.Children.Add(_keyRevocationPrompt);

        panel.Children.Add(SectionTitle("共享设置"));
        panel.Children.Add(FieldLabel("默认主机"));
        panel.Children.Add(_settingsDefaultHostField);
        panel.Children.Add(FieldLabel("主机别名列表（每行一个）"));
        panel.Children.Add(_settingsKnownHostsField);
        panel.Children.Add(FieldLabel("历史保留条数"));
        panel.Children.Add(_settingsRetentionField);
        panel.Children.Add(FieldLabel("CONDA_EXE（远端前缀）"));
        panel.Children.Add(_settingsCondaField);
        panel.Children.Add(FieldLabel("单次执行超时（分钟）"));
        panel.Children.Add(_settingsTimeoutField);
        panel.Children.Add(new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _settingsSaveButton, _settingsResetButton }
        });
        panel.Children.Add(_settingsDirtyText);
        panel.Children.Add(_modulePreferenceHint);
        panel.Children.Add(_settingsMessage);

        panel.Children.Add(SectionTitle("执行历史"));
        panel.Children.Add(_historyText);
        panel.Children.Add(_clearHistoryButton);

        panel.Children.Add(SectionTitle("高级"));
        panel.Children.Add(_openCatalogButton);
        panel.Children.Add(_revalidateButton);
        panel.Children.Add(_backgroundHint);
        panel.Children.Add(_commandsPath);
        panel.Children.Add(_commandsFileStatus);
        panel.Children.Add(_catalogError);
        panel.Children.Add(_settingsSummary);
        panel.Children.Add(_dataDirectory);
        panel.Children.Add(Text(
            "上面“设置”里的改动会写入模块读取的同一份文件；这里显示的是模块当前生效的状态。",
            RemoteCommandsMobileTheme.NoteClass));

        return panel;
    }

    // ---------------------------------------------------------------- catalog sheet

    private Control BuildCatalogPanel()
    {
        _catalogSaveButton.Click += async (_, _) => await _viewModel.SaveCatalogAsync();
        _catalogImportButton.Click += async (_, _) => await ImportCatalogFromClipboardAsync();
        _catalogCopyButton.Click += async (_, _) => await CopyTextAsync(_viewModel.CatalogYaml, "命令配置");
        _catalogReloadButton.Click += async (_, _) => await _viewModel.ReloadCatalogYamlAsync();

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_catalogSaveButton);
        buttons.Children.Add(_catalogImportButton);
        buttons.Children.Add(_catalogReloadButton);
        buttons.Children.Add(_catalogCopyButton);

        var panel = _catalogPanel;
        panel.Spacing = 10;
        panel.Children.Add(_catalogDirtyText);
        panel.Children.Add(_catalogYamlStatus);
        panel.Children.Add(_catalogEditor);
        panel.Children.Add(buttons);
        panel.Children.Add(_catalogSaveMessage);
        panel.Children.Add(Text(
            "这里是高级编辑：格式错误时模块会拒绝保存并说明原因，文本会保留。普通添加与修改请用命令表单。",
            RemoteCommandsMobileTheme.NoteClass));
        return panel;
    }

    // ---------------------------------------------------------------- controls

    /// <summary>
    /// Builds every control here, after the palette was selected from the Shell theme. Contract classes
    /// are added instead of local colours wherever the mobile theme covers the role.
    /// </summary>
    private void CreateControls()
    {
        _pageTitle = Text("远程命令", RemoteCommandsMobileTheme.PageTitleClass);
        _pageSubtitle = Text("", RemoteCommandsMobileTheme.PageSubtitleClass);
        _transportWarning = Text("", RemoteCommandsMobileTheme.NoteClass);
        _feedbackText = Text("", RemoteCommandsMobileTheme.NoteClass);
        _feedbackText.IsVisible = false;
        _backgroundHint = Text("", RemoteCommandsMobileTheme.MetaClass);
        _statusDetail = Text("", RemoteCommandsMobileTheme.MetaClass);

        _addCommandButton = TextButton("添加");
        _addCommandButton.Click += (_, _) => _viewModel.OpenCommandForm(null);

        _lastResultGlyph = Icon(MobileIcons.Dot);
        _lastResultEmptyGlyph = Icon(MobileIcons.Circle);
        _lastResultState = Text("", RemoteCommandsMobileTheme.RowTitleClass);
        _lastResultMeta = Text("", RemoteCommandsMobileTheme.RowMetaClass);
        _lastResultDetail = Text("", RemoteCommandsMobileTheme.NoteClass);
        _lastResultPreview = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        _lastResultPreview.Classes.Add(RemoteCommandsMobileTheme.MonoClass);
        _lastResultEmpty = Text("", RemoteCommandsMobileTheme.NoteClass);
        _lastResultCard = Card(new StackPanel());

        _commandListCaption = Text("", RemoteCommandsMobileTheme.MetaClass);
        _searchField = new TextBox { PlaceholderText = "搜索命令" };
        _searchField.Classes.Add(RemoteCommandsMobileTheme.SearchClass);
        _emptyCommandsText = Text("", RemoteCommandsMobileTheme.NoteClass);
        _noMatchesText = Text("没有匹配的命令。", RemoteCommandsMobileTheme.NoteClass);
        _noMatchesText.IsVisible = false;

        _runningState = Text("", RemoteCommandsMobileTheme.RowTitleClass);
        _runningEndpoint = Text("", RemoteCommandsMobileTheme.MetaClass);
        _runningProgress = Progress();
        _runningCancelButton = SecondaryButton("取消执行");

        _hostSummary = Text("", RemoteCommandsMobileTheme.NoteClass);
        _hostList = new ItemsControl { ItemsSource = _hostRows };
        _hostEmptyText = Text("还没有映射任何主机。", RemoteCommandsMobileTheme.NoteClass);
        _removalPrompt = Text("", RemoteCommandsMobileTheme.NoteClass);
        _removalPrompt.IsVisible = false;
        _missingAliasList = new ItemsControl { ItemsSource = _missingAliasRows };
        _missingAliasesText = Text("", RemoteCommandsMobileTheme.NoteClass);
        _hostPicker = new ComboBox { MinHeight = TouchTarget, HorizontalAlignment = HorizontalAlignment.Stretch };
        _input1Label = FieldLabel("输入 1");
        _input1Box = Field("粘贴或输入第一个输入");
        _input1Box.AcceptsReturn = true;
        _input2Label = FieldLabel("输入 2");
        _input2Box = Field("粘贴或输入第二个输入");
        _input2Box.AcceptsReturn = true;
        _secondInputToggle = Check("使用第二个输入");
        _runCommandDetail = Text("", RemoteCommandsMobileTheme.NoteClass);
        _runButton = PrimaryButton("运行");
        _runButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _cancelRunButton = SecondaryButton("取消");
        _cancelRunButton.IsVisible = false;
        _runProgress = Progress();
        _stageList = new ItemsControl { ItemsSource = _stageRows };
        _runStateGlyph = Icon(MobileIcons.Dot);
        _runState = Text("", RemoteCommandsMobileTheme.RowTitleClass);
        _runEndpoint = Text("", RemoteCommandsMobileTheme.MetaClass);
        _runMessage = Text("", RemoteCommandsMobileTheme.NoteClass);
        _runInputCaption = Text(
            "输入会写入远端临时文件，并作为 --file1/--file2 传给命令；页面不会把输入显示成执行结果。",
            RemoteCommandsMobileTheme.NoteClass);
        _permissionText = Text("", RemoteCommandsMobileTheme.NoteClass);
        _pendingEndpoint = Text("", RemoteCommandsMobileTheme.RowTitleClass);
        _pendingReason = Text("", RemoteCommandsMobileTheme.NoteClass);
        _pendingDetail = MonoSelectable();
        _pendingMessage = Text("", RemoteCommandsMobileTheme.MetaClass);
        _trustAcknowledged = Check("我已核对上面的 SHA256 指纹");
        _trustButton = PrimaryButton("信任此指纹并重新运行");
        _trustDismissButton = SecondaryButton("取消");
        _outputHeader = MonoText();
        _outputViewer = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        _outputViewer.Classes.Add(RemoteCommandsMobileTheme.MonoClass);
        _copyOutputButton = SecondaryButton("复制输出");
        _retryButton = SecondaryButton("重试");
        _editCommandButton = SecondaryButton("编辑命令");
        _runDoneButton = SecondaryButton("完成");

        _formLabelField = Field("例如：查看磁盘空间");
        _formCommandField = Field("例如：df -h");
        _formDescriptionField = Field("这条命令做什么（可选）");
        _formAdvancedToggle = SecondaryButton("更多设置（标识、类型、输入提示）");
        _formAdvancedToggle.HorizontalAlignment = HorizontalAlignment.Left;
        _formIdField = Field("自动生成，可修改");
        _formShellType = Radio("在电脑上执行", "rc-command-type", true);
        _formLocalType = Radio("手机本地转换", "rc-command-type", false);
        _formTypeHint = Text("", RemoteCommandsMobileTheme.NoteClass);
        _formHostField = Field("留空表示跟随当前主机");
        _formInput1LabelField = Field("输入 1 标签");
        _formInput1PlaceholderField = Field("输入 1 提示");
        _formSecondInputToggle = Check("需要第二个输入");
        _formInput2LabelField = Field("输入 2 标签");
        _formInput2PlaceholderField = Field("输入 2 提示");
        _saveCommandButton = PrimaryButton("保存新命令");
        _saveCommandButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _cancelCommandButton = SecondaryButton("取消");
        _deleteCommandButton = SecondaryButton("从 commands.yaml 删除");
        _deleteCommandButton.IsVisible = false;
        _commandFormMessage = Text("", RemoteCommandsMobileTheme.NoteClass);

        _aliasField = Field("例如 r743");
        _realHostField = Field("例如 100.64.0.2 或 host.example.com");
        _portField = Field("22");
        _usernameField = Field("登录用户名");
        _passwordAuth = Radio("密码", "rc-auth", true);
        _keyAuth = Radio("私钥", "rc-auth", false);
        _authHint = Text("", RemoteCommandsMobileTheme.NoteClass);
        _passwordField = Field("登录密码（仅写入系统凭据库）");
        _passwordField.PasswordChar = '●';
        _privateKeyField = Field("-----BEGIN OPENSSH PRIVATE KEY-----");
        _privateKeyField.AcceptsReturn = true;
        _privateKeyField.TextWrapping = TextWrapping.NoWrap;
        _privateKeyField.FontFamily = MonoFont;
        _passphraseField = Field("私钥口令（可留空）");
        _passphraseField.PasswordChar = '●';
        _saveHostButton = PrimaryButton("保存映射");
        _clearFormButton = SecondaryButton("清空表单");
        _formMessage = Text("", RemoteCommandsMobileTheme.NoteClass);
        _trustedKeyList = new ItemsControl { ItemsSource = _trustedKeyRows };
        _trustedKeysEmpty = Text("还没有确认过任何主机密钥。", RemoteCommandsMobileTheme.NoteClass);
        _keyRevocationPrompt = Text("", RemoteCommandsMobileTheme.NoteClass);
        _keyRevocationPrompt.IsVisible = false;
        _settingsDefaultHostField = Field("默认主机别名，例如 r743");
        _settingsKnownHostsField = Field("每行一个别名");
        _settingsKnownHostsField.AcceptsReturn = true;
        _settingsRetentionField = Field("10-5000");
        _settingsCondaField = Field("/home/user/miniconda3/bin/conda");
        _settingsCondaField.FontFamily = MonoFont;
        _settingsTimeoutField = Field("1-1440");
        _settingsDirtyText = Text("", RemoteCommandsMobileTheme.MetaClass);
        _settingsMessage = Text("", RemoteCommandsMobileTheme.NoteClass);
        _modulePreferenceHint = Text("", RemoteCommandsMobileTheme.NoteClass);
        _settingsSaveButton = PrimaryButton("保存设置");
        _settingsResetButton = SecondaryButton("还原为模块状态");
        _historyText = Text("", RemoteCommandsMobileTheme.NoteClass);
        _clearHistoryButton = SecondaryButton("清空执行历史");
        _commandsPath = MonoText();
        _commandsFileStatus = Text("", RemoteCommandsMobileTheme.NoteClass);
        _catalogError = Text("", RemoteCommandsMobileTheme.NoteClass);
        _settingsSummary = Text("", RemoteCommandsMobileTheme.MetaClass);
        _dataDirectory = MonoText();
        _revalidateButton = SecondaryButton("重新校验 commands.yaml");
        _openCatalogButton = SecondaryButton("打开命令配置（YAML 编辑器）");
        _openCatalogButton.HorizontalAlignment = HorizontalAlignment.Left;

        _catalogEditor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = MonoFont,
            MinHeight = 240,
            PlaceholderText = "id: ...\nlabel: ...\ncommand: ...\ntype: shell",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _catalogEditor.Classes.Add(RemoteCommandsMobileTheme.FieldClass);
        _catalogEditor.Bind(TextBox.TextProperty, new Binding(nameof(RemoteCommandsMobileViewModel.CatalogYaml))
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        ScrollViewer.SetHorizontalScrollBarVisibility(_catalogEditor, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_catalogEditor, ScrollBarVisibility.Auto);
        _catalogDirtyText = Text("", RemoteCommandsMobileTheme.MetaClass);
        _catalogYamlStatus = Text("", RemoteCommandsMobileTheme.MetaClass);
        _catalogSaveMessage = Text("", RemoteCommandsMobileTheme.NoteClass);
        _catalogSaveButton = PrimaryButton("保存到模块");
        _catalogImportButton = SecondaryButton("从剪贴板导入");
        _catalogReloadButton = SecondaryButton("重新载入");
        _catalogCopyButton = SecondaryButton("复制当前 YAML");

        _sheetTitle = Text("", RemoteCommandsMobileTheme.SheetTitleClass);
        _sheetSubtitle = Text("", RemoteCommandsMobileTheme.MetaClass);

        // Single-line fields commit on Enter, which is how a phone keyboard's action key behaves.
        foreach (var field in new[]
                 {
                     _formLabelField, _formCommandField, _formIdField, _formHostField, _aliasField,
                     _realHostField, _portField, _usernameField, _settingsDefaultHostField,
                     _settingsRetentionField, _settingsTimeoutField
                 })
        {
            field.AcceptsReturn = false;
            field.KeyDown += OnFieldKeyDown;
        }

        _catalogPanel = new StackPanel();
        _commandPanel = new StackPanel();
        _connectionPanel = new StackPanel();
        _runPanel = new StackPanel();
    }

    // ---------------------------------------------------------------- primitives

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, monospace");

    private static IBrush Brush(string role) => RemoteCommandsMobilePalette.Current.Brush(role);

    internal static void ToggleClass(StyledElement element, string className, bool on)
    {
        if (on)
        {
            if (!element.Classes.Contains(className))
            {
                element.Classes.Add(className);
            }

            return;
        }

        element.Classes.Remove(className);
    }

    private static TextBlock Text(string text, string className)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.Classes.Add(className);
        return block;
    }

    private static TextBlock MonoText() => Text("", RemoteCommandsMobileTheme.MonoClass);

    private static SelectableTextBlock MonoSelectable()
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        block.Classes.Add(RemoteCommandsMobileTheme.MonoClass);
        return block;
    }

    private static TextBlock SectionTitle(string text) => Text(text, RemoteCommandsMobileTheme.SectionTitleClass);

    private static TextBlock FieldLabel(string text) => Text(text, RemoteCommandsMobileTheme.FieldLabelClass);

    private static TextBox Field(string placeholder)
    {
        var field = new TextBox { PlaceholderText = placeholder };
        field.Classes.Add(RemoteCommandsMobileTheme.FieldClass);
        return field;
    }

    private static CheckBox Check(string content)
    {
        var check = new CheckBox { Content = content };
        check.Classes.Add(RemoteCommandsMobileTheme.CheckClass);
        return check;
    }

    private static RadioButton Radio(string content, string group, bool isChecked)
    {
        var radio = new RadioButton
        {
            // The theme's touch target styles target CheckBox; a radio keeps the metric locally.
            Content = content,
            GroupName = group,
            MinHeight = TouchTarget,
            IsChecked = isChecked
        };
        radio.Classes.Add(RemoteCommandsMobileTheme.CheckClass);
        return radio;
    }

    private static ProgressBar Progress()
    {
        var bar = new ProgressBar { IsIndeterminate = true };
        bar.Classes.Add(RemoteCommandsMobileTheme.ProgressClass);
        return bar;
    }

    private static Button PrimaryButton(string text)
    {
        var button = new Button { Content = text };
        button.Classes.Add(RemoteCommandsMobileTheme.PrimaryClass);
        return button;
    }

    private static Button SecondaryButton(string text)
    {
        var button = new Button { Content = text };
        button.Classes.Add(RemoteCommandsMobileTheme.SecondaryClass);
        return button;
    }

    private static Button IconButton()
    {
        var button = new Button();
        button.Classes.Add(RemoteCommandsMobileTheme.IconButtonClass);
        return button;
    }

    private static Button BackButton()
    {
        var button = new Button();
        button.Classes.Add(RemoteCommandsMobileTheme.BackButtonClass);
        return button;
    }

    private static Button CloseButton()
    {
        var button = new Button();
        button.Classes.Add(RemoteCommandsMobileTheme.CloseButtonClass);
        return button;
    }

    private static Button QuietButton(string text)
    {
        var button = new Button { Content = text };
        button.Classes.Add(RemoteCommandsMobileTheme.QuietButtonClass);
        return button;
    }

    private static Button TextButton(string text)
    {
        var button = new Button { Content = text };
        button.Classes.Add(RemoteCommandsMobileTheme.TextButtonClass);
        return button;
    }

    private static Border Card(Control child)
    {
        var border = new Border { Child = child };
        border.Classes.Add(RemoteCommandsMobileTheme.CardClass);
        return border;
    }

    /// <summary>A prototype stroke icon. No icon font is required, so it renders on every device.</summary>
    private static ShapePath Icon(string data)
    {
        var path = new ShapePath { Data = Geometry.Parse(data), VerticalAlignment = VerticalAlignment.Center };
        path.Classes.Add(RemoteCommandsMobileTheme.IconClass);
        return path;
    }
}

/// <summary>
/// Vector marks taken from the approved prototype's icon set (24 dp box, stroke only).
/// </summary>
internal static class MobileIcons
{
    public const string Back = "m15 5-7 7 7 7";
    public const string Close = "m6 6 12 12M6 18 18 6";
    public const string Refresh = "M20 9a8 8 0 0 0-14-4L3 8m0-5v5h5M4 15a8 8 0 0 0 14 4l3-3m0 5v-5h-5";
    public const string Chevron = "m9 5 7 7-7 7";
    public const string Terminal = "m5 6 5 6-5 6M13 18h6";
    public const string Code = "m8 5-6 7 6 7M16 5l6 7-6 7";
    public const string Check = "m5 12 4 4L19 6";
    public const string Failed = "m6 6 12 12M6 18 18 6";
    public const string Cancelled = "M7 7h10v10H7z";
    public const string Warning = "M12 3 2 20h20zM12 9v5M12 17h.01";
    public const string Active = "M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z";
    public const string Circle = "M12 5a7 7 0 1 0 0 14 7 7 0 0 0 0-14z";
    public const string Dot = "M12 12h.01";
}
