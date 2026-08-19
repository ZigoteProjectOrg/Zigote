namespace Zigote.Http;

/// <summary>
///     Marks an interface as an HTTP API. The generator emits a sealed implementation of it, named
///     after the interface without its leading <c>I</c> and suffixed <c>Client</c>
///     (<c>IAssetApi</c> → <c>AssetApiClient</c>).
/// </summary>
/// <remarks>
///     Retrofit's ergonomics without Retrofit's runtime proxy: the binding happens at compile time,
///     so it is trim- and AOT-clean and every binding mistake is a build error rather than a
///     surprise at the first call.
/// </remarks>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class HttpApiAttribute : Attribute
{
    /// <summary>Prefixed to every route on this interface. <c>"v1"</c> makes <c>assets/{id}</c> into <c>v1/assets/{id}</c>.</summary>
    public string BasePath { get; set; } = "";
}

/// <summary>Base of the verb attributes. Use the derived ones.</summary>
[AttributeUsage(AttributeTargets.Method)]
public abstract class HttpMethodAttribute(string template) : Attribute
{
    /// <summary>The route template, relative to the interface's <see cref="HttpApiAttribute.BasePath" />.</summary>
    public string Template { get; } = template;
}

/// <summary>A GET at <c>template</c>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GetAttribute(string template) : HttpMethodAttribute(template);

/// <summary>A HEAD at <c>template</c>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class HeadAttribute(string template) : HttpMethodAttribute(template);

/// <summary>A POST at <c>template</c>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PostAttribute(string template) : HttpMethodAttribute(template);

/// <summary>A PUT at <c>template</c>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PutAttribute(string template) : HttpMethodAttribute(template);

/// <summary>A PATCH at <c>template</c>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PatchAttribute(string template) : HttpMethodAttribute(template);

/// <summary>A DELETE at <c>template</c>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DeleteAttribute(string template) : HttpMethodAttribute(template);

/// <summary>This parameter is the request body, serialized as JSON unless it is bytes or a stream.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class BodyAttribute : Attribute;

/// <summary>This parameter is a query-string pair. The default for any parameter that binds nowhere else.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class QueryAttribute(string? name = null) : Attribute
{
    /// <summary>The wire name. Defaults to the parameter name.</summary>
    public string? Name { get; } = name;
}

/// <summary>This parameter is a request header.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class HeaderAttribute(string name) : Attribute
{
    /// <summary>The header name.</summary>
    public string Name { get; } = name;
}

/// <summary>This call bypasses the response cache in both directions.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class NoCacheAttribute : Attribute;

/// <summary>Repeating this call is safe, so a POST or PATCH may be retried.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IdempotentAttribute : Attribute;

/// <summary>This call is never retried — for endpoints where a duplicate is worse than a failure.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class NoRetryAttribute : Attribute;

/// <summary>The whole-call budget for this method, in seconds — retries and revalidation included.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DeadlineAttribute(double seconds) : Attribute
{
    /// <summary>The budget in seconds.</summary>
    public double Seconds { get; } = seconds;
}

/// <summary>This call streams its response body instead of buffering it.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class StreamingAttribute : Attribute;
