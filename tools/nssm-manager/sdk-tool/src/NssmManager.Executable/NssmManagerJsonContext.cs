using System.Text.Json.Serialization;
using NssmManager.Contracts;

namespace NssmManager.Executable;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(NssmServiceConfiguration))]
[JsonSerializable(typeof(NssmServiceSnapshot))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class NssmManagerJsonContext : JsonSerializerContext;
