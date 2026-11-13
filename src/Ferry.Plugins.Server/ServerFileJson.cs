using System.Text.Json.Serialization;

namespace Ferry.Plugins.Server;

/// <summary>Files kept on disk, which people may open and edit: indented (no reflection, so Native AOT works).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, string>), TypeInfoPropertyName = "Settings")]
[JsonSerializable(typeof(ServerLogin))]
internal sealed partial class ServerFileJson : JsonSerializerContext;
