using System.Text.Json.Serialization;
using Zigote.Runtime.Prefab;
using Zigote.Runtime.Scene;

namespace Zigote.Runtime.Serialization;

/// <summary>
///     Source-generated serializer metadata for everything the game runtime deserializes (scene +
///     project manifest + prefabs) — required under NativeAOT, where the reflection-based resolver
///     does
///     not exist. Metadata mode (not fast-path): scene options use ReferenceHandler.Preserve and
///     custom
///     Vec/Quat converters, both of which run through the metadata path. IncludeFields covers
///     <c>ZgRenderSettings3D</c>'s public native-interop fields.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    IncludeFields = true
)]
[JsonSerializable(typeof(SceneGraph))]
[JsonSerializable(typeof(ZigoteProject))]
[JsonSerializable(typeof(PrefabDocument))]
internal partial class RuntimeJsonContext : JsonSerializerContext;
