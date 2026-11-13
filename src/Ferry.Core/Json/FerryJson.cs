using System.Text.Json.Serialization;
using Ferry.Core.AppLocking;
using Ferry.Core.Download;
using Ferry.Core.Http;
using Ferry.Core.Queue;

namespace Ferry.Core.Json;

/// <summary>Source-generated JSON for every type Ferry reads or writes (no reflection, so Native AOT works).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PartState))]
internal sealed partial class FerryJson : JsonSerializerContext;

/// <summary>Files kept in the config folder, which people may open and edit: indented, enums by name.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, string>), TypeInfoPropertyName = "Settings")]
[JsonSerializable(typeof(List<SavedQueueItem>))]
[JsonSerializable(typeof(LockSecret))]
[JsonSerializable(typeof(List<Browse.WatchedAlbum>))]
[JsonSerializable(typeof(Dictionary<Guid, RequestDetails>), TypeInfoPropertyName = "SavedRequests")]
internal sealed partial class FerryFileJson : JsonSerializerContext;
