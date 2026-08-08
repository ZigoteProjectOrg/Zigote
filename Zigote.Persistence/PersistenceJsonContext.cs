using System.Text.Json.Serialization;

namespace Zigote.Persistence;

/// <summary>
///     Source-generated JSON metadata for the persistence layer's own files — keeps
///     <see cref="JsonFileKeyValueStore" /> reflection-free under NativeAOT. Indented output is
///     deliberate: store files are meant to be diffed and read by humans.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SortedDictionary<string, string>))]
internal sealed partial class PersistenceJsonContext : JsonSerializerContext
{
}