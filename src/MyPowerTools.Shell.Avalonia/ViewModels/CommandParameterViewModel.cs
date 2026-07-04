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

public sealed class CommandParameterViewModel : ObservableViewModel
{
    private string _value;
    private bool _booleanValue;
    private string _validationMessage = "";

    public CommandParameterViewModel(string id, string label, string type, bool required, string defaultValue)
    {
        Id = id;
        Label = label;
        Type = string.IsNullOrWhiteSpace(type) ? "text" : type;
        Required = required;
        _value = defaultValue;
        _booleanValue = string.Equals(defaultValue, "true", StringComparison.OrdinalIgnoreCase);
    }

    public string Id { get; }
    public string Label { get; }
    public string Type { get; }
    public bool Required { get; }
    public bool IsBoolean => Type is "bool" or "boolean" or "toggle";
    public bool IsText => !IsBoolean;
    public bool ShouldEmit => IsBoolean || Required || !string.IsNullOrWhiteSpace(Value);
    public bool HasValidationError => ValidationMessage.Length > 0;
    public string PreviewValue => IsBoolean ? (BooleanValue ? "true" : "false") : Value;

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public bool BooleanValue
    {
        get => _booleanValue;
        set => SetProperty(ref _booleanValue, value);
    }

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

    public string Validate()
    {
        if (!IsBoolean && Required && string.IsNullOrWhiteSpace(Value))
        {
            return $"{Label} is required.";
        }

        if (string.IsNullOrWhiteSpace(Value))
        {
            return "";
        }

        if (Type is "integer" && !long.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return $"{Label} must be an integer.";
        }

        if (Type is "number" && !double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return $"{Label} must be a number.";
        }

        return "";
    }

    public void SetValidationMessage(string message)
    {
        ValidationMessage = message;
    }

    public JsonNode? ToJsonNode()
    {
        if (IsBoolean)
        {
            return JsonValue.Create(BooleanValue);
        }

        if (Type is "integer" && long.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return JsonValue.Create(integer);
        }

        if (Type is "number" && double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return JsonValue.Create(number);
        }

        return JsonValue.Create(Value);
    }
}

public sealed record CommandExecutionStatus(string State, string Message, bool IsTerminal = true, int Sequence = 0);

public sealed record CommandProgressItemViewModel(int Sequence, string StateLabel, string Message, bool IsTerminal);

public sealed record CommandCancellationStatus(bool Accepted, string InvocationId, string State, string Message);
