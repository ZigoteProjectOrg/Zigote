using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Zigote.Core.Math3D;
using Zigote.Runtime.Serialization;

namespace Zigote.Runtime.Scene;

/// <summary>
///     System.Text.Json converters for the math value-types. These are immutable
///     <c>readonly struct</c>s with primary constructors and get-only properties;
///     under <see cref="System.Text.Json.Serialization.ReferenceHandler.Preserve" /> the default
///     resolver instantiates them as <c>default</c> and cannot populate the read-only
///     members, so every value deserialized as zero. Explicit converters read the
///     <c>{X,Y,Z[,W]}</c> object directly, bypassing reference-metadata handling.
/// </summary>
public static class MathJson
{
    /// <summary>
    ///     Editor-side extension point: types outside the runtime's source-gen context (e.g.
    ///     <c>PrefabDocument</c>) resolve through this fallback. The editor installs a reflection
    ///     resolver via a module initializer; the AOT player leaves it null and needs only the
    ///     generated metadata.
    /// </summary>
    public static IJsonTypeInfoResolver? ExtraResolver { get; set; }

    private static float ReadComponent(ref Utf8JsonReader reader) => reader.GetSingle();

    private static (float, float, float, float) ReadObject(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected object");
        float x = 0, y = 0, z = 0, w = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            string? name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "X" or "x": x = ReadComponent(ref reader); break;
                case "Y" or "y": y = ReadComponent(ref reader); break;
                case "Z" or "z": z = ReadComponent(ref reader); break;
                case "W" or "w": w = ReadComponent(ref reader); break;
                default: reader.Skip(); break;
            }
        }

        return (x, y, z, w);
    }

    /// <summary>
    ///     Build the shared serializer options used for scene save/load. The source-generated
    ///     resolver (not reflection) supplies type metadata so scene load works under NativeAOT.
    /// </summary>
    public static JsonSerializerOptions SceneOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions {
            WriteIndented = writeIndented,
            ReferenceHandler = ReferenceHandler.Preserve,
            TypeInfoResolver = ExtraResolver is null
                ? RuntimeJsonContext.Default
                : JsonTypeInfoResolver.Combine(RuntimeJsonContext.Default, ExtraResolver),
        };
        options.Converters.Add(new Vec2Converter());
        options.Converters.Add(new Vec3Converter());
        options.Converters.Add(new Vec4Converter());
        options.Converters.Add(new QuatConverter());
        return options;
    }

    public sealed class Vec2Converter : JsonConverter<Vec2>
    {
        public override Vec2 Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        {
            (float x, float y, _, _) = ReadObject(ref reader);
            return new Vec2(x: x, y: y);
        }

        public override void Write(Utf8JsonWriter w, Vec2 v, JsonSerializerOptions o)
        {
            w.WriteStartObject();
            w.WriteNumber(propertyName: "X", value: v.X);
            w.WriteNumber(propertyName: "Y", value: v.Y);
            w.WriteEndObject();
        }
    }

    public sealed class Vec3Converter : JsonConverter<Vec3>
    {
        public override Vec3 Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        {
            (float x, float y, float z, _) = ReadObject(ref reader);
            return new Vec3(x: x, y: y, z: z);
        }

        public override void Write(Utf8JsonWriter w, Vec3 v, JsonSerializerOptions o)
        {
            w.WriteStartObject();
            w.WriteNumber(propertyName: "X", value: v.X);
            w.WriteNumber(propertyName: "Y", value: v.Y);
            w.WriteNumber(propertyName: "Z", value: v.Z);
            w.WriteEndObject();
        }
    }

    public sealed class Vec4Converter : JsonConverter<Vec4>
    {
        public override Vec4 Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        {
            (float x, float y, float z, float w) = ReadObject(ref reader);
            return new Vec4(
                x: x,
                y: y,
                z: z,
                w: w
            );
        }

        public override void Write(Utf8JsonWriter writer, Vec4 v, JsonSerializerOptions o)
        {
            writer.WriteStartObject();
            writer.WriteNumber(propertyName: "X", value: v.X);
            writer.WriteNumber(propertyName: "Y", value: v.Y);
            writer.WriteNumber(propertyName: "Z", value: v.Z);
            writer.WriteNumber(propertyName: "W", value: v.W);
            writer.WriteEndObject();
        }
    }

    public sealed class QuatConverter : JsonConverter<Quat>
    {
        public override Quat Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        {
            (float x, float y, float z, float w) = ReadObject(ref reader);
            return new Quat(
                x: x,
                y: y,
                z: z,
                w: w
            );
        }

        public override void Write(Utf8JsonWriter writer, Quat v, JsonSerializerOptions o)
        {
            writer.WriteStartObject();
            writer.WriteNumber(propertyName: "X", value: v.X);
            writer.WriteNumber(propertyName: "Y", value: v.Y);
            writer.WriteNumber(propertyName: "Z", value: v.Z);
            writer.WriteNumber(propertyName: "W", value: v.W);
            writer.WriteEndObject();
        }
    }
}
