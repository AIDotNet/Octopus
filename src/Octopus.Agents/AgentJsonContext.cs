using System.Text.Json.Serialization;
using Octopus.Core.Agents;

namespace Octopus.Agents;

/// <summary>Source-generated JSON metadata for agent definitions and the scan cache (AOT friendly).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(AgentDefinition))]
[JsonSerializable(typeof(AgentScanResult))]
[JsonSerializable(typeof(List<AgentOption>))]
public partial class AgentJsonContext : JsonSerializerContext;
