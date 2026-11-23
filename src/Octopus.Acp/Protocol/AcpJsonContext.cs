using System.Text.Json.Serialization;

namespace Octopus.Acp.Protocol;

/// <summary>Source-generated JSON context for ACP registry caching and AOT serialization.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AcpRegistryDocument))]
[JsonSerializable(typeof(AcpRegistryAgent))]
public sealed partial class AcpJsonContext : JsonSerializerContext;
