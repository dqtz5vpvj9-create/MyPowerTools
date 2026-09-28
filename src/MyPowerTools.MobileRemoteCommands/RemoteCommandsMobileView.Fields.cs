using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace MyPowerTools.MobileRemoteCommands;

/// <summary>
/// Control fields for the code-built page. They are assigned in <c>CreateControls</c> (after the palette
/// was picked from the Shell theme) and bound to the view model in <see cref="WireBindings"/>.
/// </summary>
internal sealed partial class RemoteCommandsMobileView
{
    // page header
    private TextBlock _pageTitle = null!;
    private TextBlock _pageSubtitle = null!;
    private Border _statusPill = null!;
    private TextBlock _statusPillText = null!;
    private TextBlock _statusDetail = null!;
    private TextBlock _transportWarning = null!;
    private TextBlock _backgroundHint = null!;
    private TextBlock _feedbackText = null!;
    private Button _backButton = null!;
    private Button _refreshButton = null!;

    // running banner on the page
    private Border _runningCard = null!;
    private TextBlock _runningState = null!;
    private TextBlock _runningEndpoint = null!;
    private ProgressBar _runningProgress = null!;
    private Button _runningCancelButton = null!;

    // command list
    private TextBlock _commandListCaption = null!;
    private Border _searchBox = null!;
    private TextBox _searchField = null!;
    private readonly ObservableCollection<Control> _commandRows = [];
    private ItemsControl _commandList = null!;
    private readonly List<Button> _commandRowButtons = [];
    private Border _emptyCommandsCard = null!;
    private TextBlock _emptyCommandsText = null!;
    private TextBlock _noMatchesText = null!;
    private Button _addCommandButton = null!;

    // last result
    private Border _lastResultCard = null!;
    private ShapePath _lastResultGlyph = null!;
    private TextBlock _lastResultMeta = null!;
    private TextBlock _lastResultState = null!;
    private SelectableTextBlock _lastResultPreview = null!;
    private TextBlock _lastResultDetail = null!;
    private TextBlock _lastResultEmpty = null!;
    private ShapePath _lastResultEmptyGlyph = null!;
    private StackPanel _pagePanel = null!;
    private StackPanel _lastResultBody = null!;
    private StackPanel _lastResultEmptyPanel = null!;

    // page footer
    private Button _connectionButton = null!;
    private Button _catalogButton = null!;

    // overlay + sheet chrome
    private Grid _overlay = null!;
    private Border _scrim = null!;
    private Border _sheet = null!;
    private TextBlock _sheetTitle = null!;
    private TextBlock _sheetSubtitle = null!;
    private Button _sheetCloseButton = null!;
    private StackPanel _sheetBody = null!;

    // run sheet
    private StackPanel _runPanel = null!;
    private TextBlock _runCommandDetail = null!;
    private ComboBox _hostPicker = null!;
    private TextBlock _input1Label = null!;
    private TextBox _input1Box = null!;
    private CheckBox _secondInputToggle = null!;
    private TextBlock _input2Label = null!;
    private TextBox _input2Box = null!;
    private Button _runButton = null!;
    private Button _cancelRunButton = null!;
    private ProgressBar _runProgress = null!;
    private readonly ObservableCollection<Control> _stageRows = [];
    private ItemsControl _stageList = null!;
    private ShapePath _runStateGlyph = null!;
    private TextBlock _runState = null!;
    private TextBlock _runEndpoint = null!;
    private TextBlock _runMessage = null!;
    private Border _permissionNotice = null!;
    private TextBlock _permissionText = null!;
    private Border _pendingCard = null!;
    private TextBlock _pendingEndpoint = null!;
    private TextBlock _pendingReason = null!;
    private SelectableTextBlock _pendingDetail = null!;
    private TextBlock _pendingMessage = null!;
    private CheckBox _trustAcknowledged = null!;
    private Button _trustButton = null!;
    private Button _trustDismissButton = null!;
    private TextBlock _outputHeader = null!;
    private SelectableTextBlock _outputViewer = null!;
    private Button _copyOutputButton = null!;
    private Button _retryButton = null!;
    private Button _editCommandButton = null!;
    private Button _runDoneButton = null!;
    private TextBlock _runInputCaption = null!;

    // command editor sheet
    private StackPanel _commandPanel = null!;
    private TextBox _formLabelField = null!;
    private TextBox _formCommandField = null!;
    private TextBox _formDescriptionField = null!;
    private Button _formAdvancedToggle = null!;
    private StackPanel _formAdvancedPanel = null!;
    private TextBox _formIdField = null!;
    private RadioButton _formShellType = null!;
    private RadioButton _formLocalType = null!;
    private TextBlock _formTypeHint = null!;
    private TextBox _formHostField = null!;
    private TextBox _formInput1LabelField = null!;
    private TextBox _formInput1PlaceholderField = null!;
    private CheckBox _formSecondInputToggle = null!;
    private TextBox _formInput2LabelField = null!;
    private TextBox _formInput2PlaceholderField = null!;
    private Button _saveCommandButton = null!;
    private Button _cancelCommandButton = null!;
    private Button _deleteCommandButton = null!;
    private TextBlock _commandFormMessage = null!;

    // connection sheet
    private StackPanel _connectionPanel = null!;
    private readonly ObservableCollection<Control> _hostRows = [];
    private ItemsControl _hostList = null!;
    private TextBlock _hostEmptyText = null!;
    private TextBlock _removalPrompt = null!;
    private readonly ObservableCollection<Control> _missingAliasRows = [];
    private ItemsControl _missingAliasList = null!;
    private TextBox _aliasField = null!;
    private TextBox _realHostField = null!;
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
    private TextBlock _keyRevocationPrompt = null!;
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
    private TextBlock _hostSummary = null!;
    private TextBlock _missingAliasesText = null!;
    private TextBlock _historyText = null!;
    private Button _clearHistoryButton = null!;
    private TextBlock _commandsPath = null!;
    private TextBlock _commandsFileStatus = null!;
    private TextBlock _catalogError = null!;
    private TextBlock _settingsSummary = null!;
    private TextBlock _dataDirectory = null!;
    private Button _revalidateButton = null!;
    private Button _openCatalogButton = null!;

    // catalog (commands.yaml) sheet
    private StackPanel _catalogPanel = null!;
    private TextBox _catalogEditor = null!;
    private TextBlock _catalogDirtyText = null!;
    private TextBlock _catalogYamlStatus = null!;
    private TextBlock _catalogSaveMessage = null!;
    private Button _catalogSaveButton = null!;
    private Button _catalogImportButton = null!;
    private Button _catalogReloadButton = null!;
    private Button _catalogCopyButton = null!;

    private void WireBindings()
    {
        var vm = _viewModel;
        _searchField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.SearchText)));
        _input1Box.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.Input1)));
        _input2Box.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.Input2)));
        _secondInputToggle.Bind(ToggleButton.IsCheckedProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.ShowSecondInput)));
        _trustAcknowledged.Bind(ToggleButton.IsCheckedProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.TrustFingerprintAcknowledged)));
        _outputViewer.Bind(SelectableTextBlock.TextProperty, new Binding(nameof(RemoteCommandsMobileViewModel.OutputText)));

        _formLabelField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormLabel)));
        _formCommandField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormCommand)));
        _formDescriptionField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormDescription)));
        _formIdField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormId)));
        _formHostField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormHost)));
        _formInput1LabelField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormInput1Label)));
        _formInput1PlaceholderField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormInput1Placeholder)));
        _formInput2LabelField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormInput2Label)));
        _formInput2PlaceholderField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormInput2Placeholder)));
        _formSecondInputToggle.Bind(ToggleButton.IsCheckedProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormShowSecondInput)));
        _formShellType.Bind(ToggleButton.IsCheckedProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormUsesRemoteHost)));
        _formLocalType.Bind(ToggleButton.IsCheckedProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormUsesLocalTransform)));

        _aliasField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormAlias)));
        _realHostField.Bind(TextBox.TextProperty, TwoWay(nameof(RemoteCommandsMobileViewModel.FormRealHost)));
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
