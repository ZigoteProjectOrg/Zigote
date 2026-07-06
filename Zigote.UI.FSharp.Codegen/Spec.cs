using System.Linq.Expressions;
using System.Reflection;
using Zigote.UI.Material;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.FSharp.Codegen;

/// <summary>How a disappearing attr restores the widget default (see the F# reconciler's Unset path).</summary>
public enum UnsetKind
{
    /// <summary>No reset — the last-applied value sticks (only safe for attrs that are never conditional).</summary>
    None,

    /// <summary>Reset a <c>Nullable&lt;T&gt;</c> property to <c>Nullable()</c>.</summary>
    Nullable,

    /// <summary>Reset to an explicit F# default expression (e.g. <c>Color.Transparent</c>).</summary>
    To,
}

public readonly record struct Unset(UnsetKind Kind, string? Expr)
{
    public static readonly Unset None = new(UnsetKind.None, null);
    public static readonly Unset Nullable = new(UnsetKind.Nullable, null);

    public static Unset To(string expr)
    {
        return new Unset(UnsetKind.To, expr);
    }
}

public enum AttrKind
{
    /// <summary><c>let name (v: T) = …</c> — a parameterized property setter.</summary>
    Param,

    /// <summary><c>let name = …</c> — a preset that pins the property to a fixed value.</summary>
    Fixed,

    /// <summary>
    ///     <c>let name (handler: T -&gt; unit) = …</c> — an <c>Action</c>/<c>Action&lt;T&gt;</c>
    ///     event.
    /// </summary>
    Handler,

    /// <summary>A verbatim F# line (aliases/composites like <c>bold</c>, <c>slider.range</c>).</summary>
    Raw,
}

public sealed class AttrSpec
{
    public string? FixedLiteral; // Fixed: the F# literal the attr pins
    public AttrKind Kind;
    public string Member = "";
    public PropertyInfo? Prop; // resolved from an expression tree (null for Raw)
    public string? Raw; // Raw: verbatim F# (indented by the emitter)
    public bool Rebuild; // property is read in Build() → the setter must MarkNeedsBuild
    public Unset Unset;
}

public sealed class WidgetSpec
{
    public List<AttrSpec> Attrs = new();
    public Type Clr = typeof(object); // CLR type for reflection/coverage
    public string Module = ""; // F# module name (text, column, …)

    /// <summary>
    ///     Set for a <c>StatelessWidget</c> whose property setters call <c>Invalidate()</c> themselves
    ///     (e.g. Card). Suppresses the validation that would otherwise require every attr to
    ///     <c>rebuild: true</c> — a StatelessWidget that does NOT self-invalidate reads its props in
    ///     Build(), so a plain MarkNeedsLayout is dropped and the attr must force a rebuild.
    /// </summary>
    public bool SelfInvalidating;

    /// <summary>
    ///     Properties handled positionally by the <c>Ui.*</c> factories (Child, Text, OnChanged,
    ///     the controlled value…) — excluded from the coverage report so it lists only real candidates.
    /// </summary>
    public HashSet<string> Structural = new();

    public string WidgetType = ""; // F# type name for the setter cast (Label, Dropdown<string>, …)
}

/// <summary>
///     Fluent per-widget builder. Property references are <see cref="Expression{TDelegate}" /> so the
///     compiler resolves them against the real widget type — a typo or a renamed property fails to
///     build here, before any F# is emitted.
/// </summary>
public sealed class ModuleBuilder<T>
{
    public readonly WidgetSpec Spec;

    public ModuleBuilder(string module, string widgetType)
    {
        Spec = new WidgetSpec {
            Module = module,
            WidgetType = widgetType,
            Clr = typeof(T),
        };
    }

    private static PropertyInfo Resolve<P>(Expression<Func<T, P>> get)
    {
        var body = get.Body is UnaryExpression u ? u.Operand : get.Body;
        if (body is MemberExpression { Member: PropertyInfo pi })
            return pi;
        throw new ArgumentException($"expected a property access, got: {get}");
    }

    public ModuleBuilder<T> Prop<P>(string member, Expression<Func<T, P>> get,
        Unset unset = default,
        bool rebuild = false)
    {
        Spec.Attrs.Add(
            new AttrSpec {
                Member = member,
                Kind = AttrKind.Param,
                Prop = Resolve(get),
                Unset = unset,
                Rebuild = rebuild,
            }
        );
        return this;
    }

    public ModuleBuilder<T> Fixed<P>(string member, Expression<Func<T, P>> get, string literal,
        Unset unset = default, bool rebuild = false)
    {
        Spec.Attrs.Add(
            new AttrSpec {
                Member = member,
                Kind = AttrKind.Fixed,
                Prop = Resolve(get),
                FixedLiteral = literal,
                Unset = unset,
                Rebuild = rebuild,
            }
        );
        return this;
    }

    public ModuleBuilder<T> Handler<P>(string member, Expression<Func<T, P>> get)
    {
        Spec.Attrs.Add(
            new AttrSpec {
                Member = member,
                Kind = AttrKind.Handler,
                Prop = Resolve(get),
            }
        );
        return this;
    }

    public ModuleBuilder<T> Raw(string fsharp)
    {
        Spec.Attrs.Add(
            new AttrSpec {
                Kind = AttrKind.Raw,
                Raw = fsharp,
            }
        );
        return this;
    }

    /// <summary>Mark this StatelessWidget as self-invalidating (its setters call <c>Invalidate()</c>).</summary>
    public ModuleBuilder<T> SelfInvalidating()
    {
        Spec.SelfInvalidating = true;
        return this;
    }

    /// <summary>Declare properties handled by the <c>Ui.*</c> factories, so they don't clutter coverage.</summary>
    public ModuleBuilder<T> Structural(params string[] names)
    {
        foreach (var n in names) Spec.Structural.Add(n);
        return this;
    }
}

/// <summary>
///     The single source of truth: which widget properties become F# attrs, and the SEMANTIC metadata
///     the C# types can't express (invalidation level, unset default, handler/preset shape). Mirrors —
///     and now replaces — the former hand-written <c>Attrs.fs</c> modules.
/// </summary>
public static class WidgetSpecs
{
    private static ModuleBuilder<T> Mod<T>(string module, string widgetType)
    {
        return new ModuleBuilder<T>(module, widgetType);
    }

    public static List<WidgetSpec> All()
    {
        return new List<WidgetSpec> {
            Mod<Label>("text", "Label")
                .Structural("Text")
                .Prop("fontSize", l => l.FontSize, Unset.Nullable)
                .Prop("color", l => l.Color, Unset.Nullable)
                .Prop("weight", l => l.FontWeight, Unset.To("FontWeight.Normal"))
                .Fixed(
                    "italic",
                    l => l.FontStyle,
                    "FontStyle.Italic",
                    Unset.To("FontStyle.Normal")
                )
                .Prop("align", l => l.Align, Unset.To("TextAlign.Left"))
                .Prop("style", l => l.Style, Unset.To("Label.LabelStyle.Body"))
                .Prop("maxLines", l => l.MaxLines, Unset.Nullable)
                .Fixed(
                    "ellipsis",
                    l => l.Overflow,
                    "TextOverflow.Ellipsis",
                    Unset.To("TextOverflow.Clip")
                )
                .Prop("family", l => l.FontFamily, Unset.To("null"))
                .Prop("lineHeight", l => l.LineHeight, Unset.Nullable)
                .Prop("letterSpacing", l => l.LetterSpacing, Unset.To("0f"))
                .Raw("let bold = weight FontWeight.Bold")
                .Spec,
            Mod<Column>("column", "Column")
                .Prop("mainAxis", c => c.MainAxisAlign)
                .Prop("crossAxis", c => c.CrossAxisAlign)
                .Prop("mainAxisSize", c => c.MainAxisSize)
                .Spec,
            Mod<Row>("row", "Row")
                .Prop("mainAxis", r => r.MainAxisAlign)
                .Prop("crossAxis", r => r.CrossAxisAlign)
                .Prop("mainAxisSize", r => r.MainAxisSize)
                .Spec,
            Mod<Stack>("stack", "Stack")
                .Prop("alignment", s => s.Alignment)
                .Spec,
            Mod<Wrap>("wrap", "Wrap")
                .Prop("spacing", w => w.Spacing)
                .Prop("runSpacing", w => w.RunSpacing)
                .Fixed("vertical", w => w.Direction, "Axis.Vertical")
                .Spec,
            Mod<Button>("button", "Button")
                .Structural("Label", "OnPressed", "Content")
                .Prop("style", b => b.Style)
                .Prop("enabled", b => b.Enabled)
                // Button reads these in Build()/ApplyColors, not Paint — the reconciler's MarkNeedsLayout
                // is insufficient, so the setter must MarkNeedsBuild (rebuild: true).
                .Prop(
                    "background",
                    b => b.BackgroundColor,
                    Unset.Nullable,
                    true
                )
                .Prop(
                    "textColor",
                    b => b.TextColor,
                    Unset.Nullable,
                    true
                )
                .Prop(
                    "fontSize",
                    b => b.FontSize,
                    Unset.Nullable,
                    true
                )
                .Prop(
                    "radius",
                    b => b.Radius,
                    Unset.Nullable,
                    true
                )
                .Prop(
                    "padding",
                    b => b.Padding,
                    Unset.Nullable,
                    true
                )
                .Raw("let outlined = style ButtonStyle.Outlined")
                .Raw("let flat = style ButtonStyle.Flat")
                .Spec,
            Mod<TextField>("textField", "TextField")
                .Structural("Text", "OnChanged")
                .Prop("hint", t => t.Hint)
                .Prop("readOnly", t => t.ReadOnly)
                .Prop("obscure", t => t.Obscure)
                .Fixed("multiline", t => t.Multiline, "true")
                .Prop("minLines", t => t.MinLines)
                .Prop("maxLines", t => t.MaxLines)
                .Prop("height", t => t.Height)
                .Prop("minWidth", t => t.MinWidth)
                .Handler("onSubmit", t => t.OnSubmit)
                .Spec,
            Mod<Slider>("slider", "Slider")
                .Structural("Value", "OnChanged")
                .Prop("min", s => s.Min)
                .Prop("max", s => s.Max)
                .Prop("enabled", s => s.Enabled)
                .Raw("let range (lo: float32) (hi: float32) = [ min lo; max hi ]")
                .Spec,
            // Module name == the Ui factory name (Ui.scrollView).
            Mod<ScrollView>("scrollView", "ScrollView")
                .Structural("Child")
                .Fixed("horizontal", s => s.ScrollHorizontal, "true")
                .Fixed("noVertical", s => s.ScrollVertical, "false")
                .Prop("smooth", s => s.Smooth)
                .Spec,

            // Module name == the Ui factory name (Ui.decorated). NOT `box` — that collides with F#'s
            // built-in `box` operator, which shadows the module at every call site.
            Mod<DecoratedBox>("decorated", "DecoratedBox")
                .Structural("Child")
                .Prop("fill", b => b.Fill, Unset.To("Color.Transparent"))
                .Prop("radius", b => b.Radius, Unset.To("0f"))
                .Prop("borderColor", b => b.BorderColor, Unset.To("Color.Transparent"))
                .Prop("borderWidth", b => b.BorderWidth, Unset.To("1f"))
                .Prop("elevation", b => b.Elevation, Unset.Nullable)
                .Spec,

            // Card is a StatelessWidget but its setters call Invalidate() themselves.
            Mod<Card>("card", "Card")
                .SelfInvalidating()
                .Structural("Child")
                .Prop("padding", c => c.Padding, Unset.Nullable)
                .Prop("radius", c => c.Radius, Unset.Nullable)
                .Prop("color", c => c.Color, Unset.Nullable)
                .Prop("bordered", c => c.Bordered)
                .Spec,

            // Divider is a StatelessWidget whose plain setters DON'T self-invalidate — it reads these in
            // Build(), so the attrs must MarkNeedsBuild (rebuild:true) or a re-render is silently dropped.
            Mod<Divider>("divider", "Divider")
                .Fixed(
                    "vertical",
                    d => d.Vertical,
                    "true",
                    rebuild: true
                )
                .Prop("thickness", d => d.Thickness, rebuild: true)
                .Prop(
                    "color",
                    d => d.Color,
                    Unset.Nullable,
                    true
                )
                .Spec,
            Mod<Dropdown<string>>("dropdown", "Dropdown<string>")
                .Structural("Items", "SelectedIndex", "OnChanged")
                .Prop("height", d => d.Height)
                .Prop("minWidth", d => d.MinWidth)
                .Spec,
        };
    }
}