using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using ShapePath = Avalonia.Controls.Shapes.Path;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.MobileRemoteCommands;

/// <summary>
/// Remote Commands tool page for phones.
///
/// The page drives the <c>remote-commands-android</c> module end to end: it lists the shared
/// <c>commands.yaml</c> catalog, runs a command with the desktop command-line contract, streams progress
/// and output while it runs, cancels it, retries a failure with the same inputs, maps aliases to real
/// hosts, confirms host keys one fingerprint at a time and reports history. There is no local SSH client
/// here and no second copy of the tool state: the module owns the transport, the catalog, the host
/// catalog, the host-key store, the secret store and the data directory.
/// </summary>
internal sealed partial class RemoteCommandsMobileView
    : UserControl, IMptAvaloniaSurfaceActivationHandler, IMptAvaloniaSurfaceBackHandler
{
    private readonly MptAvaloniaSurfaceContext _context;
    private readonly RemoteCommandsMobileViewModel _viewModel;
    private bool _syncing;
    private string _commandRowSignature = "";
    private string _stageRowSignature = "";
    private Button? _hostDeleteButton;
    private Button? _hostKeyRevokeButton;
    private bool _wasSheetOpen;
    private Control? _focusBeforeSheet;

    public RemoteCommandsMobileView(MptAvaloniaSurfaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        RemoteCommandsMobilePalette.Current = RemoteCommandsMobilePalette.ForTheme(context.Theme);

        _viewModel = new RemoteCommandsMobileViewModel(context);
        DataContext = _viewModel;

        Content = BuildLayout();
        WireBindings();
        _viewModel.PropertyChanged += (_, _) => UpdateChrome();

        AttachedToVisualTree += (_, _) =>
        {
            // The page listens on its TopLevel so a hardware Escape/back key reaches it from any focus
            // position; the handler only consumes the key while the page has a layer to close.
            TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnRootKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

            // The mobile tokens are per theme variant: follow the ambient variant so an already attached
            // page re-pins the colours the theme does not own (tone marks) instead of keeping the old ones.
            ActualThemeVariantChanged += OnActualThemeVariantChanged;
            ApplyThemeVariant();
            _viewModel.Activate();
            UpdateChrome();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            TopLevel.GetTopLevel(this)?.RemoveHandler(KeyDownEvent, OnRootKeyDown);
            ActualThemeVariantChanged -= OnActualThemeVariantChanged;
            _viewModel.Deactivate();
        };

        UpdateChrome();
    }

    internal RemoteCommandsMobileViewModel ViewModel => _viewModel;

    /// <summary>
    /// Re-pins the page palette to the ambient variant and re-applies every tone, so a live
    /// light → dark → light switch updates the marks the shared theme does not colour (and never leaves
    /// the previous variant's error colour behind).
    /// </summary>
    private void OnActualThemeVariantChanged(object? sender, EventArgs e) => ApplyThemeVariant();

    private void ApplyThemeVariant()
    {
        RemoteCommandsMobilePalette.Current = RemoteCommandsMobilePalette.ForThemeVariant(ActualThemeVariant);
        UpdateChrome();
    }

    /// <summary>
    /// Page-level back (<see cref="IMptAvaloniaSurfaceBackHandler"/>). The Shell offers Back here before
    /// it leaves the tool page: <see langword="true"/> means the page consumed it (a bottom sheet was
    /// open, or a pending confirmation was cancelled).
    /// </summary>
    public bool TryHandleBack() => _viewModel.TryHandleBack();

    // ---------------------------------------------------------------- chrome

    private void UpdateChrome()
    {
        var vm = _viewModel;

        _pageTitle.Text = vm.PageTitle;
        _pageSubtitle.Text = vm.PageSubtitle;
        _statusPillText.Text = vm.StatusText;
        ToggleClass(_statusPill, RemoteCommandsMobileTheme.OfflineClass, !vm.TransportAvailable);
        ToggleClass(_statusPillText, RemoteCommandsMobileTheme.OfflineClass, !vm.TransportAvailable);
        _statusPill.IsVisible = vm.HasStatusPill;
        _statusDetail.Text = vm.StatusDetail;
        _statusDetail.IsVisible = vm.HasStatusDetail;
        _backgroundHint.Text = vm.BackgroundText;
        _transportWarning.Text = vm.TransportWarning;
        _transportWarning.IsVisible = vm.HasTransportWarning;
        ApplyTextTone(_transportWarning, "warning");
        _feedbackText.Text = vm.FeedbackText;
        _feedbackText.IsVisible = vm.HasFeedback;
        ApplyTextTone(_feedbackText, vm.FeedbackTone);
        _refreshButton.IsEnabled = vm.CanInteract;

        _runningCard.IsVisible = vm.IsRunning;
        _runningState.Text = vm.RunStateText;
        _runningEndpoint.Text = vm.RunEndpointText;
        _runningEndpoint.IsVisible = vm.HasRunEndpoint;
        _runningCancelButton.IsEnabled = vm.CanCancel;

        _commandListCaption.Text = vm.CommandListCaption;
        _searchBox.IsVisible = vm.ShowSearch;
        _emptyCommandsCard.IsVisible = vm.HasNoCommands;
        _emptyCommandsText.Text = vm.EmptyCommandsText;
        _noMatchesText.IsVisible = vm.HasNoMatches;
        _addCommandButton.IsEnabled = vm.CanInteract;
        RebuildCommandRows();

        // The card is always there: with a session result, the module's history, or an honest empty state.
        _lastResultBody.IsVisible = vm.HasLastResult;
        _lastResultEmptyPanel.IsVisible = !vm.HasLastResult;
        _lastResultEmpty.Text = vm.LastResultEmptyText;
        ApplyIconTone(_lastResultEmptyGlyph, vm.HasLastResultHistory ? "accent" : "muted");
        _lastResultGlyph.Data = Geometry.Parse(GlyphGeometry(vm.LastResultGlyph));
        ApplyIconTone(_lastResultGlyph, vm.LastResultTone);
        _lastResultState.Text = vm.LastResultStateText;
        ApplyTextTone(_lastResultState, vm.LastResultTone);
        _lastResultMeta.Text = vm.LastResultMeta;
        _lastResultDetail.Text = vm.LastResultDetailText;
        _lastResultDetail.IsVisible = vm.LastResultDetailText.Length > 0;
        _lastResultPreview.Text = vm.LastResultPreview;

        _commandsPath.Text = vm.CommandsPath.Length == 0 ? "" : $"commands.yaml：{vm.CommandsPath}";
        _commandsPath.IsVisible = vm.CommandsPath.Length > 0;
        _commandsFileStatus.Text = vm.CommandsFileStatus;
        _catalogError.Text = vm.CatalogError;
        _catalogError.IsVisible = vm.HasCatalogError;
        ApplyTextTone(_catalogError, "error");
        _settingsSummary.Text = vm.SettingsSummary;
        _dataDirectory.Text = vm.DataDirectory.Length == 0 ? "" : $"数据目录：{vm.DataDirectory}";
        _dataDirectory.IsVisible = vm.DataDirectory.Length > 0;

        UpdateSheetChrome();
        UpdateRunChrome();
        UpdateCommandFormChrome();
        UpdateConnectionChrome();
        UpdateCatalogChrome();
    }

    private void UpdateSheetChrome()
    {
        var vm = _viewModel;

        // The theme contract asks for focus restoration after an overlay: remember what had focus when
        // the sheet opened and give it back when the sheet closes.
        if (vm.IsSheetOpen && !_wasSheetOpen)
        {
            _focusBeforeSheet = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        }
        else if (!vm.IsSheetOpen && _wasSheetOpen)
        {
            _focusBeforeSheet?.Focus();
            _focusBeforeSheet = null;
        }

        _wasSheetOpen = vm.IsSheetOpen;

        _overlay.IsVisible = vm.IsSheetOpen;
        _sheetTitle.Text = vm.SheetTitle;
        _sheetSubtitle.Text = vm.SheetSubtitle;
        _runPanel.IsVisible = vm.IsRunSheetOpen;
        _commandPanel.IsVisible = vm.IsCommandSheetOpen;
        _connectionPanel.IsVisible = vm.IsConnectionSheetOpen;
        _catalogPanel.IsVisible = vm.IsCatalogSheetOpen;
    }

    private void UpdateRunChrome()
    {
        var vm = _viewModel;

        _runCommandDetail.Text = vm.CommandDetailText;
        _input1Label.Text = vm.Input1Label;
        _input1Box.PlaceholderText = vm.Input1Placeholder;
        _input1Box.IsEnabled = vm.CanInteract;
        _secondInputToggle.IsEnabled = vm.CanInteract;
        _input2Label.Text = vm.Input2Label;
        _input2Label.IsVisible = vm.ShowSecondInput;
        _input2Box.IsVisible = vm.ShowSecondInput;
        _runInputCaption.IsVisible = vm.SelectedCommand?.IsLocalTransform == false;

        _syncing = true;
        try
        {
            _hostPicker.ItemsSource = vm.HostPickerItems;
            _hostPicker.SelectedItem = vm.HostPickerItems.FirstOrDefault(choice =>
                string.Equals(choice.Alias, vm.SelectedHost, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _syncing = false;
        }

        _hostPicker.IsEnabled = vm.CanInteract && vm.HasHosts;
        _runButton.Content = vm.IsRunning ? "正在执行…" : "运行";
        _runButton.IsEnabled = vm.CanRun && (!vm.HasTransportWarning || vm.SelectedCommand?.IsLocalTransform == true);
        _cancelRunButton.IsVisible = vm.IsRunning;
        _cancelRunButton.IsEnabled = vm.CanCancel;
        _runProgress.IsVisible = vm.IsRunning;
        RebuildStageRows();

        _runStateGlyph.Data = Geometry.Parse(GlyphGeometry(vm.RunGlyph));
        ApplyIconTone(_runStateGlyph, vm.RunTone);
        _runState.Text = vm.RunStateText;
        ApplyTextTone(_runState, vm.RunTone);
        _runEndpoint.Text = vm.RunEndpointText;
        _runEndpoint.IsVisible = vm.HasRunEndpoint;
        _runMessage.Text = vm.RunMessageText;
        _runMessage.IsVisible = vm.HasRunMessage;

        // The detail line follows the run's tone: a finished run must not read as a failure line, and a
        // failure keeps the error tone.
        ApplyTextTone(_runMessage, vm.RunTone == "success" ? "info" : vm.RunTone);
        _permissionNotice.IsVisible = vm.HasRunPermissionNotice;
        _permissionText.Text = vm.RunPermissionText;
        ApplyTextTone(_permissionText, "warning");

        _pendingCard.IsVisible = vm.HasPendingHostKey;
        _pendingEndpoint.Text = vm.PendingHostKeyEndpoint;
        _pendingReason.Text = vm.PendingHostKeyReason;
        _pendingDetail.Text = vm.PendingHostKeyDetail;
        _pendingMessage.Text = vm.PendingHostKeyMessage;
        _trustAcknowledged.IsEnabled = vm.CanInteract;
        _trustButton.IsEnabled = vm.CanTrustPendingHostKey;
        _trustDismissButton.IsEnabled = vm.CanInteract;

        _outputHeader.Text = vm.OutputHeaderText;
        _outputHeader.IsVisible = vm.OutputHeaderText.Length > 0;
        _copyOutputButton.IsEnabled = vm.HasOutput;
        _copyOutputButton.IsVisible = vm.HasOutput;
        _retryButton.IsVisible = vm.HasLastResult && !vm.IsRunning && !vm.HasPendingHostKey;
        _retryButton.IsEnabled = vm.CanRetry;
        _editCommandButton.IsVisible = vm.SelectedCommand is not null;
        _editCommandButton.IsEnabled = vm.CanInteract;
        _runDoneButton.IsEnabled = vm.CanInteract;
    }

    private void UpdateCommandFormChrome()
    {
        var vm = _viewModel;
        _formAdvancedPanel.IsVisible = vm.CommandFormAdvanced;
        _formAdvancedToggle.Content = vm.CommandAdvancedToggleText;
        _formTypeHint.Text = vm.FormTypeHint;
        _formTypeHint.IsVisible = vm.CommandFormAdvanced;
        _saveCommandButton.Content = vm.CommandFormSaveLabel;
        _saveCommandButton.IsEnabled = vm.CanSaveCommandForm;
        _cancelCommandButton.IsEnabled = vm.CanInteract;
        _deleteCommandButton.IsVisible = vm.IsEditingExistingCommand;
        _deleteCommandButton.Content = vm.CommandDeleteLabel;
        _commandFormMessage.Text = vm.CommandFormMessage;
        _commandFormMessage.IsVisible = vm.HasCommandFormMessage;
        ApplyTextTone(_commandFormMessage, "warning");
    }

    private void UpdateConnectionChrome()
    {
        var vm = _viewModel;
        RebuildHostRows();
        _hostSummary.Text = vm.HostSummaryText;
        _hostEmptyText.IsVisible = vm.HasNoHosts;
        _removalPrompt.Text = vm.RemovalPrompt;
        _removalPrompt.IsVisible = vm.HasPendingRemoval;
        _missingAliasesText.Text = vm.MissingAliasesText;
        _missingAliasesText.IsVisible = vm.HasMissingAliases;
        RebuildMissingAliasRows();

        _saveHostButton.IsEnabled = vm.CanInteract;
        _clearFormButton.IsEnabled = vm.CanInteract;
        _authHint.Text = vm.FormAuthText;
        _passwordField.IsVisible = vm.FormUsesPassword;
        _passphraseField.IsVisible = vm.FormUsesPrivateKey;
        _privateKeyField.IsVisible = vm.FormUsesPrivateKey;
        _formMessage.Text = vm.FormMessage;
        _formMessage.IsVisible = vm.HasFormMessage;
        ApplyTextTone(_formMessage, "info");

        RebuildTrustedKeyRows();
        _trustedKeysEmpty.IsVisible = !vm.HasTrustedKeys;
        _keyRevocationPrompt.Text = vm.KeyRevocationPrompt;
        _keyRevocationPrompt.IsVisible = vm.HasPendingKeyRevocation;

        // The five settings controls stay editable while a refresh runs: they hold the user's draft,
        // and only the save button reflects the busy state (so a click can explain itself).
        _settingsDirtyText.Text = vm.SettingsDirtyText;
        _settingsMessage.Text = vm.SettingsMessage;
        _settingsMessage.IsVisible = vm.HasSettingsMessage;
        ApplyTextTone(_settingsMessage, "info");
        _modulePreferenceHint.Text = vm.ModuleSettingsHint;
        _settingsSaveButton.IsEnabled = vm.CanSaveSettings;
        _settingsResetButton.IsEnabled = vm.CanInteract;

        _historyText.Text = vm.HistoryText;
        _clearHistoryButton.Content = vm.HistoryClearPrompt;
        _clearHistoryButton.IsEnabled = vm.CanInteract;
        _revalidateButton.IsEnabled = vm.CanInteract;
        _openCatalogButton.IsEnabled = vm.CanInteract;
    }

    private void UpdateCatalogChrome()
    {
        var vm = _viewModel;
        _catalogDirtyText.Text = vm.CatalogDirtyText;
        _catalogYamlStatus.Text = vm.CatalogYamlStatus;
        _catalogSaveMessage.Text = vm.CatalogSaveMessage;
        _catalogSaveMessage.IsVisible = vm.HasCatalogSaveMessage;
        ApplyTextTone(_catalogSaveMessage, "warning");
        _catalogSaveButton.IsEnabled = vm.CanSaveCatalog;
        _catalogImportButton.IsEnabled = vm.CanInteract;
        _catalogReloadButton.IsEnabled = vm.CanInteract;
        _catalogReloadButton.Content = vm.CatalogReloadLabel;
        _catalogCopyButton.IsEnabled = vm.CatalogYaml.Length > 0;
    }

    // ---------------------------------------------------------------- rows

    private void RebuildCommandRows()
    {
        var commands = _viewModel.VisibleCommands;
        var signature = string.Join('\u0001', commands.Select(command => $"{command.Id}:{command.Label}:{command.ListSubtitleText}")) + "\u0002" + _viewModel.SearchText;
        if (signature == _commandRowSignature)
        {
            return;
        }

        _commandRowSignature = signature;
        _commandRows.Clear();
        _commandRowButtons.Clear();

        for (var index = 0; index < commands.Count; index++)
        {
            var command = commands[index];
            var button = new Button();
            button.Classes.Add(RemoteCommandsMobileTheme.ListRowClass);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var badge = new Border { VerticalAlignment = VerticalAlignment.Center };
            badge.Classes.Add(RemoteCommandsMobileTheme.IconBoxClass);
            var badgeIcon = Icon(command.IsLocalTransform ? MobileIcons.Code : MobileIcons.Terminal);
            badgeIcon.Classes.Add(RemoteCommandsMobileTheme.IconAccentClass);
            ToggleClass(badgeIcon, RemoteCommandsMobileTheme.IconMutedClass, false);
            badgeIcon.HorizontalAlignment = HorizontalAlignment.Center;
            badge.Child = badgeIcon;
            grid.Children.Add(badge);

            var copy = new StackPanel { Spacing = 2, Margin = new Thickness(12, 0, 8, 0) };
            copy.Children.Add(Text(command.Label, RemoteCommandsMobileTheme.RowTitleClass));
            copy.Children.Add(Text(command.ListSubtitleText, RemoteCommandsMobileTheme.RowSubtitleClass));
            Grid.SetColumn(copy, 1);
            grid.Children.Add(copy);

            var chevron = Icon(MobileIcons.Chevron);
            chevron.Classes.Add(RemoteCommandsMobileTheme.IconMutedClass);
            Grid.SetColumn(chevron, 2);
            grid.Children.Add(chevron);

            button.Content = grid;
            button.Click += (_, _) => _viewModel.OpenRunSheet(command);
            _commandRowButtons.Add(button);

            // M1's row divider carries the separator, so the list card never sets a local colour.
            if (index > 0)
            {
                var divider = new Border();
                divider.Classes.Add(RemoteCommandsMobileTheme.RowDividerClass);
                _commandRows.Add(divider);
            }

            _commandRows.Add(button);
        }
    }

    private void RebuildStageRows()
    {
        var stages = _viewModel.RunStages;
        var signature = string.Join('\u0001', stages.Select(stage => $"{stage.Title}:{stage.State}"));
        if (signature == _stageRowSignature)
        {
            return;
        }

        _stageRowSignature = signature;
        _stageRows.Clear();
        foreach (var stage in stages)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var glyph = Icon(GlyphGeometry(stage.Glyph));
            ApplyIconTone(glyph, stage.Tone);
            glyph.Margin = new Thickness(0, 0, 10, 0);
            grid.Children.Add(glyph);

            var copy = new StackPanel { Spacing = 1 };
            copy.Children.Add(Text(stage.Title, RemoteCommandsMobileTheme.RowTitleClass));
            copy.Children.Add(Text(stage.Detail, RemoteCommandsMobileTheme.RowSubtitleClass));
            Grid.SetColumn(copy, 1);
            grid.Children.Add(copy);
            _stageRows.Add(grid);
        }
    }

    private void RebuildHostRows()
    {
        _hostRows.Clear();
        _hostDeleteButton = null;
        foreach (var host in _viewModel.Hosts)
        {
            var confirming = _viewModel.PendingRemoval is { } pending &&
                             string.Equals(pending.Alias, host.Alias, StringComparison.OrdinalIgnoreCase);

            var edit = SecondaryButton("编辑");
            edit.Click += (_, _) => _viewModel.PrefillHost(host.Alias);

            var remove = SecondaryButton(confirming ? "确认删除" : "删除");
            remove.Click += async (_, _) =>
            {
                if (confirming)
                {
                    await _viewModel.ConfirmHostRemovalAsync();
                    return;
                }

                _viewModel.RequestHostRemoval(host);
            };

            var copy = new StackPanel { Spacing = 2 };
            copy.Children.Add(Text(host.Alias, RemoteCommandsMobileTheme.RowTitleClass));
            copy.Children.Add(Text(host.SubtitleText, RemoteCommandsMobileTheme.RowSubtitleClass));
            copy.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { edit, remove } });

            _hostDeleteButton = remove;
            var panel = new Border { Child = copy, Margin = new Thickness(0, 0, 0, 8) };
            panel.Classes.Add(RemoteCommandsMobileTheme.InsetPanelClass);
            _hostRows.Add(panel);
        }
    }

    private void RebuildMissingAliasRows()
    {
        _missingAliasRows.Clear();
        foreach (var alias in _viewModel.MissingAliases)
        {
            var map = SecondaryButton("映射");
            map.Click += (_, _) => _viewModel.PrefillHost(alias);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 4) };
            var label = Text(alias, RemoteCommandsMobileTheme.RowTitleClass);
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
            Grid.SetColumn(map, 1);
            row.Children.Add(map);
            _missingAliasRows.Add(row);
        }
    }

    private void RebuildTrustedKeyRows()
    {
        _trustedKeyRows.Clear();
        _hostKeyRevokeButton = null;
        foreach (var key in _viewModel.TrustedKeys)
        {
            var revoking = _viewModel.PendingKeyRevocation is { } pending &&
                           string.Equals(pending.EndpointText, key.EndpointText, StringComparison.OrdinalIgnoreCase);

            var revoke = SecondaryButton(revoking ? "确认撤销" : "撤销");
            revoke.Click += async (_, _) =>
            {
                if (revoking)
                {
                    await _viewModel.RevokeHostKeyAsync(key);
                    return;
                }

                _viewModel.RequestHostKeyRevocation(key);
            };

            var copy = new StackPanel { Spacing = 2 };
            copy.Children.Add(Text(key.EndpointText, RemoteCommandsMobileTheme.RowTitleClass));
            copy.Children.Add(Text(key.SubtitleText, RemoteCommandsMobileTheme.RowSubtitleClass));
            copy.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { revoke } });

            _hostKeyRevokeButton = revoke;
            var panel = new Border { Child = copy, Margin = new Thickness(0, 0, 0, 8) };
            panel.Classes.Add(RemoteCommandsMobileTheme.InsetPanelClass);
            _trustedKeyRows.Add(panel);
        }
    }

    // ---------------------------------------------------------------- keyboard

    /// <summary>
    /// Single-line fields submit on Enter, exactly like the phone keyboard's action key. Multi-line
    /// inputs keep Enter for newlines.
    /// </summary>
    private void OnFieldKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key is not (Key.Enter or Key.Return))
        {
            return;
        }

        args.Handled = true;
        DismissKeyboard();
        if (sender is TextBox field)
        {
            if (IsCommandFormField(field))
            {
                _ = SaveCommandFromControlsAsync();
                return;
            }

            if (IsHostFormField(field))
            {
                _ = _viewModel.SaveHostAsync();
                return;
            }

            if (IsSettingsField(field))
            {
                _ = SaveSettingsFromControlsAsync();
                return;
            }
        }
    }

    /// <summary>Android back / Escape: close the top sheet first, then let the host leave the page.</summary>
    private void OnRootKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key is not (Key.Escape or Key.Back))
        {
            return;
        }

        if (OnBackRequested())
        {
            args.Handled = true;
        }
    }

    /// <summary>The single back path: the host's back key, Escape and the tests all call this.</summary>
    private bool OnBackRequested() => _viewModel.TryHandleBack();

    private bool IsCommandFormField(TextBox field) =>
        field == _formLabelField || field == _formCommandField || field == _formIdField ||
        field == _formHostField || field == _formInput1LabelField || field == _formInput1PlaceholderField ||
        field == _formInput2LabelField || field == _formInput2PlaceholderField;

    private bool IsHostFormField(TextBox field) =>
        field == _aliasField || field == _realHostField || field == _portField || field == _usernameField;

    private bool IsSettingsField(TextBox field) =>
        field == _settingsDefaultHostField || field == _settingsKnownHostsField ||
        field == _settingsRetentionField || field == _settingsCondaField || field == _settingsTimeoutField;

    /// <summary>Hides the soft keyboard by moving focus off the text field that owns it.</summary>
    private void DismissKeyboard() => _sheetCloseButton.Focus();

    /// <summary>
    /// Save click path: the visible control text is committed to the draft first, so a save can never
    /// validate a value the user has already replaced on screen.
    /// </summary>
    private async Task SaveSettingsFromControlsAsync()
    {
        _viewModel.CommitSettingsDraft(
            _settingsDefaultHostField.Text ?? "",
            _settingsKnownHostsField.Text ?? "",
            _settingsRetentionField.Text ?? "",
            _settingsCondaField.Text ?? "",
            _settingsTimeoutField.Text ?? "");
        await _viewModel.SaveSettingsAsync();
    }

    /// <summary>Run click path: the keyboard goes away first so progress and output stay visible.</summary>
    private async Task RunFromControlsAsync()
    {
        DismissKeyboard();
        await _viewModel.RunAsync();
    }

    /// <summary>
    /// Save click path for the command form: the same "visible text wins" rule as the settings form, so a
    /// save can never write a value the user already replaced on screen.
    /// </summary>
    private async Task SaveCommandFromControlsAsync()
    {
        _viewModel.FormLabel = _formLabelField.Text ?? "";
        _viewModel.FormCommand = _formCommandField.Text ?? "";
        _viewModel.FormDescription = _formDescriptionField.Text ?? "";
        _viewModel.FormId = _formIdField.Text ?? "";
        _viewModel.FormHost = _formHostField.Text ?? "";
        _viewModel.FormInput1Label = _formInput1LabelField.Text ?? "";
        _viewModel.FormInput1Placeholder = _formInput1PlaceholderField.Text ?? "";
        _viewModel.FormInput2Label = _formInput2LabelField.Text ?? "";
        _viewModel.FormInput2Placeholder = _formInput2PlaceholderField.Text ?? "";
        await _viewModel.SaveCommandFormAsync();
    }

    // ---------------------------------------------------------------- test seams

    internal TextBox SettingsRetentionFieldForTests => _settingsRetentionField;

    internal TextBox SettingsKnownHostsFieldForTests => _settingsKnownHostsField;

    internal Button SettingsSaveButtonForTests => _settingsSaveButton;

    internal TextBox Input1FieldForTests => _input1Box;

    internal TextBox Input2FieldForTests => _input2Box;

    internal CheckBox SecondInputToggleForTests => _secondInputToggle;

    internal SelectableTextBlock OutputViewerForTests => _outputViewer;

    internal TextBlock RunStateForTests => _runState;

    internal Button RunButtonForTests => _runButton;

    internal Button CancelRunButtonForTests => _cancelRunButton;

    internal Button RetryButtonForTests => _retryButton;

    internal Button EditCommandButtonForTests => _editCommandButton;

    internal Button AddCommandButtonForTests => _addCommandButton;

    internal Button ConnectionButtonForTests => _connectionButton;

    internal Button BackButtonForTests => _backButton;

    internal Button RefreshButtonForTests => _refreshButton;

    internal Button SheetCloseButtonForTests => _sheetCloseButton;

    internal Border LastResultCardForTests => _lastResultCard;

    internal StackPanel PagePanelForTests => _pagePanel;

    internal TextBlock PageSubtitleForTests => _pageSubtitle;

    internal Button CatalogButtonForTests => _catalogButton;

    internal Button SaveCommandButtonForTests => _saveCommandButton;

    internal Button DeleteCommandButtonForTests => _deleteCommandButton;

    internal Button FormAdvancedToggleForTests => _formAdvancedToggle;

    internal TextBox CommandLabelFieldForTests => _formLabelField;

    internal TextBox CommandCommandFieldForTests => _formCommandField;

    internal TextBox CommandIdFieldForTests => _formIdField;

    internal TextBox CommandHostFieldForTests => _formHostField;

    internal Button TrustHostKeyButtonForTests => _trustButton;

    internal CheckBox TrustAcknowledgedForTests => _trustAcknowledged;

    internal Button HostKeyRevokeButtonForTests =>
        _hostKeyRevokeButton ?? throw new InvalidOperationException("还没有主机密钥行。");

    internal Button HostDeleteButtonForTests =>
        _hostDeleteButton ?? throw new InvalidOperationException("还没有主机映射行。");

    internal Button SaveHostButtonForTests => _saveHostButton;

    internal Button ClearHistoryButtonForTests => _clearHistoryButton;

    internal IReadOnlyList<Button> CommandRowButtonsForTests => _commandRowButtons;

    /// <summary>The tone-bearing state marks (run state, last result, empty state).</summary>
    internal IReadOnlyList<ShapePath> StateIconsForTests => [_runStateGlyph, _lastResultGlyph, _lastResultEmptyGlyph];

    internal string StageSummaryForTests =>
        string.Join("|", _viewModel.RunStages.Select(stage => $"{stage.Title}:{stage.State}"));

    /// <summary>Runs exactly what the save button click runs.</summary>
    internal Task SaveSettingsForTestsAsync() => SaveSettingsFromControlsAsync();

    internal Task SaveCommandForTestsAsync() => SaveCommandFromControlsAsync();

    /// <summary>Runs the same back path a hardware back key uses.</summary>
    internal bool PressBackForTests() => OnBackRequested();

    private Task CopyOutputAsync() => CopyTextAsync(_viewModel.OutputText, "输出");

    /// <summary>Pastes clipboard text into the commands.yaml editor; saving still goes through the module.</summary>
    private async Task ImportCatalogFromClipboardAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            using var data = await clipboard.TryGetDataAsync();
            var text = data is null ? null : await data.TryGetTextAsync();
            _viewModel.ImportCatalogText(text ?? "");
        }
        catch (Exception exception)
        {
            _context.Log(new MptSurfaceLogEntry("warning", $"读取剪贴板失败：{exception.Message}", DateTimeOffset.Now));
        }
    }

    private async Task CopyTextAsync(string text, string label)
    {
        if (text.Length == 0 || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(text));
            await clipboard.SetDataAsync(transfer);
            await clipboard.FlushAsync();
        }
        catch (Exception exception)
        {
            _context.Log(new MptSurfaceLogEntry("warning", $"复制{label}失败：{exception.Message}", DateTimeOffset.Now));
        }
    }

    // ---------------------------------------------------------------- activation

    /// <summary>
    /// Deep link convention for this surface:
    /// <c>mypowertools://remote-command?command=&lt;id&gt;[&amp;host=&lt;alias&gt;][&amp;run=1][&amp;input1=&lt;text&gt;][&amp;input2=&lt;text&gt;]</c>.
    /// Only a command id that exists in the shared catalog is accepted; <c>run=1</c> starts it, anything
    /// else only preselects it.
    /// </summary>
    public async ValueTask<bool> ActivateAsync(ToolActivationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Uri.TryCreate(request.ActivationUri, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("mypowertools", StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("remote-command", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = ParseQuery(uri.Query);
        var commandId = First(query, "command", "commandId");
        if (commandId.Length == 0)
        {
            return false;
        }

        if (_viewModel.Commands.Count == 0)
        {
            await _viewModel.RefreshAsync().ConfigureAwait(true);
        }

        var command = _viewModel.Commands.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, commandId, StringComparison.OrdinalIgnoreCase));
        if (command is null)
        {
            _context.Log(new MptSurfaceLogEntry(
                "warning",
                $"激活失败：commands.yaml 中没有命令 '{commandId}'。",
                DateTimeOffset.Now));
            return false;
        }

        _viewModel.OpenRunSheet(command);

        var host = First(query, "host");
        if (host.Length > 0)
        {
            _viewModel.SelectedHost = host;
        }

        var input1 = First(query, "input1");
        if (input1.Length > 0)
        {
            _viewModel.Input1 = input1;
        }

        var input2 = First(query, "input2");
        if (input2.Length > 0)
        {
            _viewModel.Input2 = input2;
            _viewModel.ShowSecondInput = true;
        }

        if (!IsTruthy(First(query, "run")))
        {
            return true;
        }

        await _viewModel.RunAsync().ConfigureAwait(true);
        return true;
    }

    private static string First(IReadOnlyDictionary<string, string> query, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (query.TryGetValue(key, out var value) && value.Length > 0)
            {
                return value;
            }
        }

        return "";
    }

    private static bool IsTruthy(string value) =>
        value.Equals("1", StringComparison.Ordinal) ||
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("yes", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = component.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0]);
            result[key] = pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "";
        }

        return result;
    }

    /// <summary>
    /// State text carries its meaning in a shared tone class; only the error tone has no SDK class, so
    /// that one is pinned from the contract palette (and cleared again when the tone changes).
    /// </summary>
    private static void ApplyTextTone(TextBlock block, string tone)
    {
        ToggleClass(block, RemoteCommandsMobileTheme.SuccessTextClass, tone == "success");
        ToggleClass(block, RemoteCommandsMobileTheme.WarningTextClass, tone == "warning");
        ToggleClass(block, RemoteCommandsMobileTheme.FadedTextClass, tone is "info" or "muted");
        ToggleClass(block, RemoteCommandsMobileTheme.AccentTextClass, tone == "accent");
        if (tone == "error")
        {
            block.Foreground = Brush("Error");
            return;
        }

        block.ClearValue(TextBlock.ForegroundProperty);
    }

    private static void ApplyIconTone(ShapePath path, string tone)
    {
        ToggleClass(path, RemoteCommandsMobileTheme.IconSuccessClass, tone == "success");
        ToggleClass(path, RemoteCommandsMobileTheme.IconWarningClass, tone == "warning");
        ToggleClass(path, RemoteCommandsMobileTheme.IconAccentClass, tone is "accent" or "active");
        ToggleClass(path, RemoteCommandsMobileTheme.IconMutedClass, tone is "info" or "muted" or "pending");
        if (tone == "error")
        {
            path.Stroke = Brush("Error");
            return;
        }

        path.ClearValue(Avalonia.Controls.Shapes.Shape.StrokeProperty);
    }

    /// <summary>Maps the view model's state marks to the prototype's vector set.</summary>
    private static string GlyphGeometry(string glyph) => glyph switch
    {
        "✓" => MobileIcons.Check,
        "✗" => MobileIcons.Failed,
        "■" => MobileIcons.Cancelled,
        "⚠" => MobileIcons.Warning,
        "●" => MobileIcons.Active,
        _ => MobileIcons.Circle
    };
}
