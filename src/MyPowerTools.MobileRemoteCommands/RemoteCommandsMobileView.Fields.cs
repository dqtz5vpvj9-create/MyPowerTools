using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;

namespace MyPowerTools.MobileRemoteCommands;

/// <summary>
/// Control fields for the code-built page. They are assigned in <c>CreateControls</c> (after the palette
/// was picked from the Shell theme) and bound to the view model in <see cref="WireBindings"/>.
/// </summary>
internal sealed partial class RemoteCommandsMobileView
{
    private TextBlock _pageTitle = null!;
    private TextBlock _statusPill = null!;
    private TextBlock _statusDetail = null!;
    private TextBlock _transportWarning = null!;
    private TextBlock _backgroundHint = null!;
    private TextBlock _feedbackText = null!;
    private Button _refreshButton = null!;
    private Button _cancelButton = null!;

    private ComboBox _commandPicker = null!;
    private TextBlock _commandDetail = null!;
    private ComboBox _hostPicker = null!;
    private TextBlock _missingAliases = null!;
    private TextBox _input1Box = null!;
    private CheckBox _secondInputToggle = null!;
    private TextBlock _input2Label = null!;
    private TextBox _input2Box = null!;
    private Button _runButton = null!;
    private Button _runCancelButton = null!;
    private TextBlock _runState = null!;
    private TextBlock _runEndpoint = null!;
    private TextBlock _runMessage = null!;
    private SelectableTextBlock _outputViewer = null!;
    private Button _copyOutputButton = null!;

    private Border _pendingCard = null!;
    private TextBlock _pendingEndpoint = null!;
    private TextBlock _pendingReason = null!;
    private SelectableTextBlock _pendingDetail = null!;
    private TextBlock _pendingMessage = null!;
    private CheckBox _trustAcknowledged = null!;
    private Button _trustButton = null!;
    private Button _trustDismissButton = null!;

    private readonly ObservableCollection<Control> _hostRows = [];
    private ItemsControl _hostList = null!;
    private TextBlock _removalPrompt = null!;
    private Button _refreshAliasesButton = null!;
    private readonly ObservableCollection<Control> _missingAliasRows = [];
    private ItemsControl _missingAliasList = null!;
    private TextBox _aliasField = null!;
    private TextBox _hostField = null!;
    private TextBox _portField = null!;
    private TextBox _usernameField = null!;
    private RadioButton _passwordAuth = null!;
    private RadioButton _keyAuth = null!;
    private TextBlock _authHint = null!;
    private TextBox _passwordField = null!;
    private TextBox _privateKeyField = null!;
    private TextBox _passphraseField = null!;
    private Button _saveHostButton = null!;
    private Button _clearFormButton = null!;
    private TextBlock _formMessage = null!;

    private readonly ObservableCollection<Control> _trustedKeyRows = [];
    private ItemsControl _trustedKeyList = null!;
    private TextBlock _trustedKeysEmpty = null!;

    private TextBlock _historyText = null!;
    private Button _clearHistoryButton = null!;

    private Border _catalogEditorCard = null!;
    private Button _catalogToggleButton = null!;
    private StackPanel _catalogEditorBody = null!;
    private TextBox _catalogEditor = null!;
    private TextBlock _catalogDirtyText = null!;
    private TextBlock _catalogYamlStatus = null!;
    private TextBlock _catalogSaveMessage = null!;
    private Button _catalogSaveButton = null!;
    private Button _catalogImportButton = null!;
    private Button _catalogReloadButton = null!;
    private Button _catalogCopyButton = null!;

    private TextBox _settingsDefaultHostField = null!;
    private TextBox _settingsKnownHostsField = null!;
    private TextBox _settingsRetentionField = null!;
    private TextBox _settingsCondaField = null!;
    private TextBox _settingsTimeoutField = null!;
    private TextBlock _settingsDirtyText = null!;
    private TextBlock _settingsMessage = null!;
    private TextBlock _modulePreferenceHint = null!;
    private Button _settingsSaveButton = null!;
    private Button _settingsResetButton = null!;

    private TextBlock _commandsPath = null!;
    private TextBlock _commandsFileStatus = null!;
    private TextBlock _catalogError = null!;
    private TextBlock _settingsSummary = null!;
    private TextBlock _dataDirectory = null!;
    private Button _revalidateButton = null!;

    private void WireBindings()
    {
        var vm = _viewModel;
        _input1Box.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.Input1)));
        _input2Box.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.Input2)));
        _secondInputToggle.Bind(ToggleButton.IsCheckedProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.ShowSecondInput)));
        _outputViewer.Bind(SelectableTextBlock.TextProperty, new Binding(nameof(RemoteCommandsMobileViewModel.OutputText)));

        _aliasField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormAlias)));
        _hostField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormHost)));
        _portField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormPort)));
        _usernameField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormUsername)));
        _passwordField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormPassword)));
        _privateKeyField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormPrivateKey)));
        _passphraseField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormPassphrase)));
        _settingsDefaultHostField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.SettingsDefaultHost)));
        _settingsKnownHostsField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.SettingsKnownHosts)));
        _settingsRetentionField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.SettingsRetention)));
        _settingsCondaField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.SettingsCondaExecutable)));
        _settingsTimeoutField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.SettingsTimeoutMinutes)));
        _passwordAuth.Bind(ToggleButton.IsCheckedProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormUsesPassword)));
        _keyAuth.Bind(ToggleButton.IsCheckedProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormUsesPrivateKey)));

        DataContext = vm;
    }

    private static Binding TwoWay(string propertyName) => new(propertyName)
    {
        Mode = BindingMode.TwoWay,
        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
    };
}
