using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.MobileRemoteCommands;

/// <summary>
/// Remote Commands tool page for phones.
///
/// The page drives the <c>remote-commands-android</c> module end to end: it lists the shared
/// <c>commands.yaml</c> catalog, runs a command with the desktop command-line contract, streams output
/// while it runs, cancels it, maps aliases to real hosts, confirms host keys one fingerprint at a time
/// and reports history. There is no local SSH client here and no second copy of the tool state: the
/// module owns the transport, the catalog, the host catalog, the host-key store, the secret store and
/// the data directory.
/// </summary>
internal sealed partial class RemoteCommandsMobileView : UserControl, IMptAvaloniaSurfaceActivationHandler
{
    private readonly MptAvaloniaSurfaceContext _context;
    private readonly RemoteCommandsMobileViewModel _viewModel;
    private bool _syncing;

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
            _viewModel.Activate();
            UpdateChrome();
        };
        DetachedFromVisualTree += (_, _) => _viewModel.Deactivate();

        UpdateChrome();
    }

    internal RemoteCommandsMobileViewModel ViewModel => _viewModel;

    // ---------------------------------------------------------------- chrome

    private void UpdateChrome()
    {
        var vm = _viewModel;

        _pageTitle.Text = vm.PageTitle;
        _statusPill.Text = vm.StatusText;
        _statusPill.Foreground = Brush(vm.TransportAvailable ? "Success" : "Error");
        _statusDetail.Text = vm.StatusDetail;
        _transportWarning.Text = vm.TransportWarning;
        _transportWarning.IsVisible = vm.HasTransportWarning;
        _backgroundHint.Text = vm.BackgroundText;
        _feedbackText.Text = vm.FeedbackText;
        _feedbackText.IsVisible = vm.FeedbackText.Length > 0;
        _feedbackText.Foreground = Brush(vm.FeedbackTone switch
        {
            "success" => "Success",
            "warning" => "Warning",
            "error" => "Error",
            _ => "Muted"
        });
        _refreshButton.IsEnabled = vm.CanInteract;
        _cancelButton.IsEnabled = vm.CanCancel;
        _cancelButton.IsVisible = vm.IsRunning;

        // Pickers are synced from the view model, so re-entrant SelectionChanged calls are ignored.
        _syncing = true;
        try
        {
            _commandPicker.ItemsSource = vm.Commands;
            _commandPicker.SelectedItem = vm.SelectedCommand;
            _hostPicker.ItemsSource = vm.HostPickerItems;
            _hostPicker.SelectedItem = vm.HostPickerItems.FirstOrDefault(choice =>
                string.Equals(choice.Alias, vm.SelectedHost, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _syncing = false;
        }

        _commandPicker.IsEnabled = vm.CanInteract && vm.HasCommands;
        _commandDetail.Text = vm.CommandDetailText;
        _hostPicker.IsEnabled = vm.CanInteract && vm.HasHosts;
        _missingAliases.Text = vm.MissingAliasesText;
        _missingAliases.IsVisible = vm.HasMissingAliases;

        _input1Box.Watermark = vm.Input1Placeholder;
        _input2Box.Watermark = vm.Input2Placeholder;
        _input2Label.Text = vm.Input2Label;
        _input2Label.IsVisible = vm.ShowSecondInput;
        _input2Box.IsVisible = vm.ShowSecondInput;
        _secondInputToggle.IsEnabled = vm.CanInteract;

        _runButton.IsEnabled = vm.CanRun && (!vm.HasTransportWarning || vm.SelectedCommand?.IsLocalTransform == true);
        _runCancelButton.IsEnabled = vm.CanCancel;
        _runState.Text = vm.RunStateText;
        _runEndpoint.Text = vm.RunEndpointText;
        _runEndpoint.IsVisible = vm.RunEndpointText.Length > 0;
        _runMessage.Text = vm.RunMessageText;
        _runMessage.IsVisible = vm.HasRunMessage;
        _copyOutputButton.IsEnabled = vm.HasOutput;

        _pendingCard.IsVisible = vm.HasPendingHostKey;
        _pendingEndpoint.Text = vm.PendingHostKeyEndpoint;
        _pendingReason.Text = vm.PendingHostKeyReason;
        _pendingDetail.Text = vm.PendingHostKeyDetail;
        _pendingMessage.Text = vm.PendingHostKeyMessage;
        _trustAcknowledged.IsChecked = vm.TrustFingerprintAcknowledged;
        _trustAcknowledged.IsEnabled = vm.CanInteract;
        _trustButton.IsEnabled = vm.CanTrustPendingHostKey;
        _trustDismissButton.IsEnabled = vm.CanInteract;

        RebuildHostRows();
        _removalPrompt.Text = vm.RemovalPrompt;
        _removalPrompt.IsVisible = vm.HasPendingRemoval;
        RebuildMissingAliasRows();
        _saveHostButton.IsEnabled = vm.CanInteract;
        _clearFormButton.IsEnabled = vm.CanInteract;
        _authHint.Text = vm.FormAuthText;
        _passwordField.IsVisible = vm.FormUsesPassword;
        _passphraseField.IsVisible = vm.FormUsesPrivateKey;
        _privateKeyField.IsVisible = vm.FormUsesPrivateKey;
        _formMessage.Text = vm.FormMessage;
        _formMessage.IsVisible = vm.HasFormMessage;

        RebuildTrustedKeyRows();
        _trustedKeysEmpty.IsVisible = !vm.HasTrustedKeys;

        _historyText.Text = vm.HistoryText;
        _clearHistoryButton.Content = vm.HistoryClearPrompt;
        _clearHistoryButton.IsEnabled = vm.CanInteract;

        _catalogToggleButton.Content = vm.CatalogEditorToggleText;
        _catalogEditorBody.IsVisible = vm.CatalogEditorExpanded;
        _catalogDirtyText.Text = vm.CatalogDirtyText;
        _catalogYamlStatus.Text = vm.CatalogYamlStatus;
        _catalogSaveMessage.Text = vm.CatalogSaveMessage;
        _catalogSaveMessage.IsVisible = vm.HasCatalogSaveMessage;
        _catalogSaveButton.IsEnabled = vm.CanSaveCatalog;
        _catalogImportButton.IsEnabled = vm.CanInteract;
        _catalogReloadButton.IsEnabled = vm.CanInteract;
        _catalogCopyButton.IsEnabled = vm.CatalogYaml.Length > 0;

        // The five settings controls stay editable while a refresh runs: they hold the user's draft,
        // and only the save button reflects the busy state (so a click can explain itself).
        _settingsDirtyText.Text = vm.SettingsDirtyText;
        _settingsMessage.Text = vm.SettingsMessage;
        _settingsMessage.IsVisible = vm.HasSettingsMessage;
        _modulePreferenceHint.Text = vm.ModuleSettingsHint;
        _settingsSaveButton.IsEnabled = vm.CanSaveSettings;
        _settingsResetButton.IsEnabled = vm.CanInteract;

        _commandsPath.Text = vm.CommandsPath.Length == 0 ? "" : $"commands.yaml：{vm.CommandsPath}";
        _commandsPath.IsVisible = vm.CommandsPath.Length > 0;
        _commandsFileStatus.Text = vm.CommandsFileStatus;
        _catalogError.Text = vm.CatalogError;
        _catalogError.IsVisible = vm.HasCatalogError;
        _settingsSummary.Text = vm.SettingsSummary;
        _dataDirectory.Text = vm.DataDirectory.Length == 0 ? "" : $"数据目录：{vm.DataDirectory}";
        _revalidateButton.IsEnabled = vm.CanInteract;
    }

    private void RebuildHostRows()
    {
        _hostRows.Clear();
        if (!_viewModel.HasHosts)
        {
            _hostRows.Add(Hint("还没有映射任何主机。请在下面添加，或对未映射的别名点“映射”。"));
            return;
        }

        foreach (var host in _viewModel.Hosts)
        {
            var confirming = _viewModel.PendingRemoval is { } pending &&
                             string.Equals(pending.Alias, host.Alias, StringComparison.OrdinalIgnoreCase);

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

            var body = new StackPanel { Spacing = 4 };
            body.Children.Add(new TextBlock
            {
                Text = host.Alias,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush("Text")
            });
            body.Children.Add(Hint($"{host.EndpointText} · {host.AuthText} · {host.CredentialText}"));
            body.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { remove } });

            _hostRows.Add(new Border
            {
                Background = Brush("Inset"),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = body
            });
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
            var label = new TextBlock
            {
                Text = alias,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("Text")
            };
            row.Children.Add(label);
            Grid.SetColumn(map, 1);
            row.Children.Add(map);
            _missingAliasRows.Add(row);
        }
    }

    private void RebuildTrustedKeyRows()
    {
        _trustedKeyRows.Clear();
        foreach (var key in _viewModel.TrustedKeys)
        {
            var revoke = SecondaryButton("撤销");
            revoke.Click += async (_, _) => await _viewModel.RevokeHostKeyAsync(key);

            var body = new StackPanel { Spacing = 4 };
            body.Children.Add(new TextBlock
            {
                Text = key.EndpointText,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush("Text")
            });
            body.Children.Add(Hint(key.FingerprintText));
            if (key.AddedAtText.Length > 0)
            {
                body.Children.Add(Hint(key.AddedAtText));
            }

            body.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { revoke } });

            _trustedKeyRows.Add(new Border
            {
                Background = Brush("Inset"),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = body
            });
        }
    }

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

    // ---------------------------------------------------------------- test seams

    internal TextBox SettingsRetentionFieldForTests => _settingsRetentionField;

    internal TextBox SettingsKnownHostsFieldForTests => _settingsKnownHostsField;

    internal Button SettingsSaveButtonForTests => _settingsSaveButton;

    /// <summary>Runs exactly what the save button click runs.</summary>
    internal Task SaveSettingsForTestsAsync() => SaveSettingsFromControlsAsync();

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

        _viewModel.SelectedCommand = command;
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
}
