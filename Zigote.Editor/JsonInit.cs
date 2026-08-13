using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;
using Zigote.Runtime.Scene;

namespace Zigote.Editor;

internal static class JsonInit
{
    /// <summary>
    ///     Editor-only types serialize through MathJson.SceneOptions but are not in the
    ///     runtime's source-gen context; the editor always runs JIT, so chain the reflection
    ///     resolver as fallback. Runs when the editor assembly loads — before any use.
    ///     (PrefabDocument moved to the runtime context so exported games can spawn prefabs.)
    /// </summary>
    [ModuleInitializer]
    internal static void InstallReflectionJsonFallback() =>
        MathJson.ExtraResolver = new DefaultJsonTypeInfoResolver();
}
