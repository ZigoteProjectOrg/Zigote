# Zigote.UI.Localizations

A flexible, declarative, modular localization (i18n) framework for `Zigote.UI` — a widget-tree
`Localizations` provider + ICU `MessageFormat`, in dependency-light C#. It references
only `Zigote.UI`; the pure-logic core (locales, plural rules, message formatting, catalogs,
number/date formatting) is headless and unit-tested (206 tests).

## The three pieces

1. **Declare** your translations as a `LocalizationBundle` of `LocalizationCatalog`s (in code, or
   loaded from JSON), or plug in typed `LocalizationsDelegate<T>`s for advanced/generated resources.
2. **Provide** them by wrapping your `Home` in a `LocalizationsScope`.
3. **Consume** them from any widget via `context.Tr("key", …)` / `LocalizedText`, and switch locale at
   runtime with `context.SetLocale(...)` — dependent widgets rebuild automatically (the reactive path
   the theme uses).

## Quick start

```csharp
var bundle = new LocalizationBundle(
    new LocalizationCatalog(Locale.En)
    {
        ["app.title"] = "My App",
        ["greeting"]  = "Hello, {name}!",
        ["items"]     = "{count, plural, =0 {No items} one {# item} other {# items}}",
    },
    new LocalizationCatalog(Locale.Es)
    {
        ["app.title"] = "Mi Aplicación",
        ["greeting"]  = "¡Hola, {name}!",
        ["items"]     = "{count, plural, =0 {Sin elementos} one {# elemento} other {# elementos}}",
    })
{
    FallbackLocale = Locale.En,
};

new ZigoteApp
{
    Home = new LocalizationsScope
    {
        Bundle           = bundle,
        SupportedLocales = { Locale.En, Locale.Es },
        // InitialLocale omitted -> resolves the OS locale against SupportedLocales
        Child            = new MyHomePage(),
    },
}.Run();
```

Inside a widget:

```csharp
protected override Widget Build(BuildContext ctx) => new Column
{
    Children =
    {
        new LocalizedText("greeting", ("name", user.Name)) { FontSize = 20f },
        new Label(ctx.Tr("items", ("count", cart.Count))),
        new Button("Español", () => ctx.SetLocale(Locale.Es)),  // live switch
    },
};
```

## Message format (ICU-lite)

| Form               | Example                                                                       |
|--------------------|-------------------------------------------------------------------------------|
| Placeholder        | `Hello, {name}!`                                                              |
| Plural             | `{n, plural, =0 {none} one {# item} other {# items}}`                         |
| Plural offset      | `{n, plural, offset:1 one {you and # other} other {you and # others}}`        |
| Ordinal            | `{pos, selectordinal, one {#st} two {#nd} few {#rd} other {#th}}`             |
| Select (gender, …) | `{g, select, male {He} female {She} other {They}}`                            |
| Typed number       | `{price, number, currency}` · `{r, number, percent}` · `{n, number, integer}` |
| Typed date/time    | `{d, date, medium}` · `{t, time, short}`                                      |
| Escaping           | `it''s` → `it's`, `'{'literal'}'` → `{literal}`                               |

`#` renders the (offset-adjusted) count; nested submessages compose (a `plural` inside a `select`,
etc.). Plural categories follow the Unicode CLDR rules — English/Germanic, Romance, Slavic (ru/uk/pl/
cs/sk), Arabic, Hebrew, Baltic, Romanian, Indic, and the "no-plural" languages are implemented
exactly; unlisted languages default to the English one/other split. A malformed template never
crashes the UI — it falls back to its raw text.

## Loading from JSON

```csharp
// Flat, per-locale (ARB-compatible; @metadata keys ignored):
var en = LocalizationJson.LoadCatalog(enJson, Locale.En);

// Nested, multi-locale:  { "en": { "hi": "Hello" }, "es": { "hi": "Hola" } }
var bundle = LocalizationJson.LoadBundle(allJson);
```

## Formatting

`context.FormatNumber/FormatInteger/FormatPercent/FormatCurrency/FormatDate/FormatTime` (and
`LocaleFormatting.For(locale)`) format according to the active locale's `CultureInfo`, degrading
gracefully for exotic tags.

## Text direction

`Locale.TextDirection` and the `Directionality` provider expose LTR/RTL (a `LocalizationsScope`
installs one from the active locale). Direction-aware widgets read `Directionality.Of(ctx)` and mirror
their own layout — the base paint path does not auto-mirror.

## Typed delegates

For generated/strongly-typed resource classes, register a `LocalizationsDelegate<T>` and retrieve it
with `Localizations.Of<T>(ctx)`. The string bundle is itself exposed this way
(`Localizations.Of<StringLocalizations>(ctx)`), so both styles coexist in one scope.
