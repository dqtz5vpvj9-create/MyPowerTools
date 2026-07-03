using System.Text.Json.Nodes;
using Google.Protobuf.WellKnownTypes;

namespace MyPowerTools.HostControl;

public static class JsonStructMapper
{
    public static Struct ToStruct(JsonObject json)
    {
        var result = new Struct();
        foreach (var pair in json)
        {
            result.Fields[pair.Key] = ToValue(pair.Value);
        }

        return result;
    }

    public static JsonObject ToJsonObject(Struct? value)
    {
        var result = new JsonObject();
        if (value is null)
        {
            return result;
        }

        foreach (var pair in value.Fields)
        {
            result[pair.Key] = ToJsonNode(pair.Value);
        }

        return result;
    }

    private static Value ToValue(JsonNode? node)
    {
        if (node is null)
        {
            return new Value { NullValue = NullValue.NullValue };
        }

        if (node is JsonObject obj)
        {
            return new Value { StructValue = ToStruct(obj) };
        }

        if (node is JsonArray array)
        {
            var list = new ListValue();
            foreach (var item in array)
            {
                list.Values.Add(ToValue(item));
            }

            return new Value { ListValue = list };
        }

        var scalar = node.AsValue();
        if (scalar.TryGetValue<string>(out var s))
        {
            return new Value { StringValue = s };
        }

        if (scalar.TryGetValue<bool>(out var b))
        {
            return new Value { BoolValue = b };
        }

        if (scalar.TryGetValue<double>(out var d))
        {
            return new Value { NumberValue = d };
        }

        if (scalar.TryGetValue<int>(out var i))
        {
            return new Value { NumberValue = i };
        }

        if (scalar.TryGetValue<long>(out var l))
        {
            return new Value { NumberValue = l };
        }

        return new Value { StringValue = scalar.ToJsonString() };
    }

    private static JsonNode? ToJsonNode(Value value)
    {
        return value.KindCase switch
        {
            Value.KindOneofCase.NullValue => null,
            Value.KindOneofCase.NumberValue => JsonValue.Create(value.NumberValue),
            Value.KindOneofCase.StringValue => JsonValue.Create(value.StringValue),
            Value.KindOneofCase.BoolValue => JsonValue.Create(value.BoolValue),
            Value.KindOneofCase.StructValue => ToJsonObject(value.StructValue),
            Value.KindOneofCase.ListValue => new JsonArray(value.ListValue.Values.Select(ToJsonNode).ToArray()),
            _ => null
        };
    }
}
