using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Octopus.Core.Plugins;
using Octopus.Core.Projects;
using Octopus.Core.Settings;

namespace Octopus.Storage;

/// <summary>Source-generated JSON metadata for the files Octopus writes. Use <see cref="FileOptions"/> to (de)serialize.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    Converters = [typeof(OctopusJsonContext.KindConverter), typeof(OctopusJsonContext.PluginSourceConverter), typeof(OctopusJsonContext.FileAccessConverter)])]
[JsonSerializable(typeof(ProjectsDocument))]
[JsonSerializable(typeof(AppSettingsModel))]
[JsonSerializable(typeof(PluginsDocument))]
public sealed partial class OctopusJsonContext : JsonSerializerContext
{
    /// <summary>Writes <see cref="ProjectItemKind"/> as a camelCase string.</summary>
    public sealed class KindConverter() : JsonStringEnumConverter<ProjectItemKind>(JsonNamingPolicy.CamelCase);

    public sealed class PluginSourceConverter() : JsonStringEnumConverter<PluginSource>(JsonNamingPolicy.CamelCase);

    public sealed class FileAccessConverter() : JsonStringEnumConverter<FileAccessKind>(JsonNamingPolicy.CamelCase);

    /// <summary>The context's options with the computed <c>DisplayTitle</c> property removed from the contract.</summary>
    public static JsonSerializerOptions FileOptions => _fileOptions ??= CreateOptions();

    private static JsonSerializerOptions? _fileOptions;

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(Default.Options)
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = Default.WithAddedModifier(info =>
            {
                if (info.Type == typeof(ProjectItem))
                {
                    for (var i = info.Properties.Count - 1; i >= 0; i--)
                    {
                        if (info.Properties[i].Name == "displayTitle") info.Properties.RemoveAt(i);
                    }
                }
            })
        };
        options.MakeReadOnly();
        return options;
    }
}
