using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Google.Protobuf.WellKnownTypes;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed class SettingsFieldViewModel : ObservableViewModel
{
    private string _value;
    private bool _booleanValue;
    private string _selectedOption;
    private string _validationMessage = "";

    public SettingsFieldViewModel(
        string key,
        string label,
        string editorType,
        string description,
        string value,
        bool booleanValue,
        IReadOnlyList<string> options,
        string selectedOption)
    {
        Key = key;
        Label = label;
        EditorType = editorType;
        Description = description;
        _value = value;
        _booleanValue = booleanValue;
        Options = options;
        _selectedOption = selectedOption;
        OriginalValue = value;
        OriginalBooleanValue = booleanValue;
        OriginalSelectedOption = selectedOption;
        RefreshValidationState();
    }

    public string Key { get; }
    public string Label { get; }
    public string EditorType { get; }
    public string Description { get; }
    public IReadOnlyList<string> Options { get; }
    public string OriginalValue { get; private set; }
    public bool OriginalBooleanValue { get; private set; }
    public string OriginalSelectedOption { get; private set; }
    public bool IsBooleanEditor => EditorType == "boolean";
    public bool IsEnumEditor => EditorType == "enum";
    public bool IsMultilineEditor => EditorType is "object" or "array";
    public bool IsSingleLineTextEditor => !IsBooleanEditor && !IsEnumEditor && !IsMultilineEditor;
    public bool IsDirty => EditorType switch
    {
        "boolean" => BooleanValue != OriginalBooleanValue,
        "enum" => !string.Equals(SelectedOption, OriginalSelectedOption, StringComparison.Ordinal),
        _ => !string.Equals(Value, OriginalValue, StringComparison.Ordinal)
    };
    public string DirtySummary => IsDirty
        ? $"{Key}: {OriginalEditorValue} -> {CurrentEditorValue}"
        : $"{Key}: unchanged";
    public string CurrentEditorValue => EditorType switch
    {
        "boolean" => BooleanValue ? "true" : "false",
        "enum" => SelectedOption,
        _ => Value
    };
    public string OriginalEditorValue => EditorType switch
    {
        "boolean" => OriginalBooleanValue ? "true" : "false",
        "enum" => OriginalSelectedOption,
        _ => OriginalValue
    };
    public bool HasValidationError => ValidationMessage.Length > 0;

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationError));
            }
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
            {
                RefreshValidationState();
                RaiseDirtyStateChanged();
            }
        }
    }

    public bool BooleanValue
    {
        get => _booleanValue;
        set
        {
            if (SetProperty(ref _booleanValue, value))
            {
                RefreshValidationState();
                RaiseDirtyStateChanged();
            }
        }
    }

    public string SelectedOption
    {
        get => _selectedOption;
        set
        {
            if (SetProperty(ref _selectedOption, value))
            {
                RefreshValidationState();
                RaiseDirtyStateChanged();
            }
        }
    }

    public string Validate()
    {
        if (EditorType == "integer" && !long.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return $"{Label} must be an integer.";
        }

        if (EditorType == "number" && !double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return $"{Label} must be a number.";
        }

        if (EditorType == "object" && !TryParseCompositeSetting(Value, JsonValueKind.Object))
        {
            return $"{Label} must be a JSON object.";
        }

        if (EditorType == "array" && !TryParseCompositeSetting(Value, JsonValueKind.Array))
        {
            return $"{Label} must be a JSON array.";
        }

        if (EditorType == "enum" && Options.Count > 0 && !Options.Contains(SelectedOption, StringComparer.Ordinal))
        {
            return $"{Label} must match one of the declared options.";
        }

        return "";
    }

    public void RefreshValidationState()
    {
        ValidationMessage = Validate();
    }

    public void AcceptCurrentValue()
    {
        OriginalValue = Value;
        OriginalBooleanValue = BooleanValue;
        OriginalSelectedOption = SelectedOption;
        OnPropertyChanged(nameof(OriginalValue));
        OnPropertyChanged(nameof(OriginalBooleanValue));
        OnPropertyChanged(nameof(OriginalSelectedOption));
        OnPropertyChanged(nameof(OriginalEditorValue));
        RaiseDirtyStateChanged();
    }

    private void RaiseDirtyStateChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(DirtySummary));
        OnPropertyChanged(nameof(CurrentEditorValue));
    }

    private static bool TryParseCompositeSetting(string value, JsonValueKind expectedKind)
    {
        var fallback = expectedKind == JsonValueKind.Object ? "{}" : "[]";
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value;
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == expectedKind;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
