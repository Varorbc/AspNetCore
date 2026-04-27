# Localization support for Microsoft.Extensions.Validation

Issues:

- https://github.com/dotnet/aspnetcore/issues/12158
- https://github.com/dotnet/aspnetcore/issues/64626

Design doc: https://github.com/dotnet/aspnetcore/blob/oroztocil/validation-localization2/src/Validation/design_public.md

Implementation branch (WIP): https://github.com/dotnet/aspnetcore/blob/oroztocil/validation-localization2

API changes, divided per sub-problems:

1) Localizer provider configuration (allows configuring resource file strategy - per type, shared - for resx-backed localizers, or to support more complex localizer setups (including composition of localizers)

- `Microsoft.Extensions.Validation.ValidationOptions.LocalizerProvider.get -> System.Func<System.Type!, Microsoft.Extensions.Localization.IStringLocalizerFactory!, Microsoft.Extensions.Localization.IStringLocalizer!>?`
- `Microsoft.Extensions.Validation.ValidationOptions.LocalizerProvider.set -> void`

2) Optional programmatic generation of error message keys (to support localization/customization of messages for all/unconfigured attribute instances without specifying message/key on each use)

- `Microsoft.Extensions.Validation.ValidationOptions.ErrorMessageKeyProvider.get -> System.Func<Microsoft.Extensions.Validation.ErrorMessageKeyContext, string?>?`
- `Microsoft.Extensions.Validation.ValidationOptions.ErrorMessageKeyProvider.set -> void`
- `Microsoft.Extensions.Validation.ErrorMessageKeyContext`
- `Microsoft.Extensions.Validation.ErrorMessageKeyContext.ErrorMessageKeyContext() -> void`
- `Microsoft.Extensions.Validation.ErrorMessageKeyContext.Attribute.get -> System.ComponentModel.DataAnnotations.ValidationAttribute!`
- `Microsoft.Extensions.Validation.ErrorMessageKeyContext.Attribute.init -> void`
- `Microsoft.Extensions.Validation.ErrorMessageKeyContext.DeclaringType.get -> System.Type?`
- `Microsoft.Extensions.Validation.ErrorMessageKeyContext.DeclaringType.init -> void`
- `Microsoft.Extensions.Validation.ErrorMessageKeyContext.DisplayName.get -> string!`
- `Microsoft.Extensions.Validation.ErrorMessageKeyContext.DisplayName.init -> void`
- `Microsoft.Extensions.Validation.ErrorMessageKeyContext.MemberName.get -> string!`
- `Microsoft.Extensions.Validation.ErrorMessageKeyContext.MemberName.init -> void`

3) Formatting of error messages with attribute-specific arguments (see adapters in MVC)

- `Microsoft.Extensions.Validation.IValidationAttributeFormatter`
- `Microsoft.Extensions.Validation.IValidationAttributeFormatter.FormatErrorMessage(System.Globalization.CultureInfo! culture, string! messageTemplate, string! displayName) -> string!`
- `Microsoft.Extensions.Validation.ValidationAttributeFormatterRegistry`
- `Microsoft.Extensions.Validation.ValidationAttributeFormatterRegistry.ValidationAttributeFormatterRegistry() -> void`
- `Microsoft.Extensions.Validation.ValidationAttributeFormatterRegistry.AddFormatter<TAttribute>(System.Func<TAttribute!, Microsoft.Extensions.Validation.IValidationAttributeFormatter!>! factory) -> void`
- `Microsoft.Extensions.Validation.ValidationAttributeFormatterRegistry.GetFormatter(System.ComponentModel.DataAnnotations.ValidationAttribute! attribute) -> Microsoft.Extensions.Validation.IValidationAttributeFormatter?`
- `Microsoft.Extensions.Validation.ValidationOptions.AttributeFormatters.get -> Microsoft.Extensions.Validation.ValidationAttributeFormatterRegistry!`

---

## Background and Motivation

`Microsoft.Extensions.Validation` currently provides no localization or message customization features beyond those built into `System.ComponentModel.DataAnnotations`. The only `DataAnnotations` localization mechanism is static property localization via `ErrorMessageResourceType`/`ErrorMessageResourceName`, which requires annotating each attribute instance individually — a verbose approach that also requires recompilation and is impractical for loading translations from databases, JSON files, or other external sources.

ASP.NET Core MVC worked around this with its own `IStringLocalizer`-based adapter system, but it is tightly coupled to MVC's model metadata pipeline and cannot be reused in Minimal APIs, Blazor, or non-web hosts. It also silently skips localization for custom attributes and lacks convention-based key selection.

This proposal adds localization support to `Microsoft.Extensions.Validation` that:

- Integrates with `Microsoft.Extensions.Localization` (`IStringLocalizer`) for runtime localization of error messages and display names
- Provides a mechanism for formatting localized templates with attribute-specific arguments (a framework-indpendent replacement for MVC's attribute adapter system)
- Supports convention-based key selection without requiring explicit `ErrorMessage` on every attribute instance
- Works consistently across Minimal APIs, Blazor, and any other consumer of `Microsoft.Extensions.Validation`

See [dotnet/aspnetcore#12158](https://github.com/dotnet/aspnetcore/issues/12158) (Blazor localization) and [dotnet/aspnetcore#64626](https://github.com/dotnet/aspnetcore/issues/64626) (Minimal API localization) for the original issues.

## Proposed API

Localization is enabled by registering an `IStringLocalizerFactory` implementation into DI (e.g., by calling `AddLocalization()`). When `IStringLocalizerFactory` is available, the validation pipeline automatically uses it to localize error messages and display names. No localization occurs when the factory is not registered.

### 1. `IStringLocalizer` resolution configuration

```diff
namespace Microsoft.Extensions.Validation;

public class ValidationOptions
{
+    /// <summary>
+    /// Controls which IStringLocalizer is used for a given declaring type.
+    /// </summary>
+    public Func<Type, IStringLocalizerFactory, IStringLocalizer>? LocalizerProvider { get; set; }
}
```

### 2. Message formatting with attribute-specific arguments

```diff
namespace Microsoft.Extensions.Validation;

+/// <summary>
+/// Formats a localized error message template with attribute-specific arguments.
+/// Replaces the MVC adapter pattern with a simpler, framework-independent interface.
+/// </summary>
+[Experimental]
+public interface IValidationAttributeFormatter
+{
+    string FormatErrorMessage(CultureInfo culture, string messageTemplate, string displayName);
+}

+/// <summary>
+/// Keyed registry of attribute formatter factories. Built-in formatters for standard
+/// attributes with multi-placeholder templates are registered automatically.
+/// </summary>
+[Experimental]
+public sealed class ValidationAttributeFormatterRegistry
+{
+    public void AddFormatter<TAttribute>(Func<TAttribute, IValidationAttributeFormatter> factory)
+        where TAttribute : ValidationAttribute;
+    public IValidationAttributeFormatter? GetFormatter(ValidationAttribute attribute);
+}

public class ValidationOptions
{
+    [Experimental]
+    public ValidationAttributeFormatterRegistry AttributeFormatters { get; }
}
```

### 3. Optional improvement: Programmatic error message keys

```diff
namespace Microsoft.Extensions.Validation;

public class ValidationOptions
{
+    /// <summary>
+    /// Controls the resource key used to look up the error message template.
+    /// Called as a fallback for attributes without an explicit ErrorMessage,
+    /// enabling convention-based key selection.
+    /// </summary>
+    public Func<ErrorMessageKeyContext, string?>? ErrorMessageKeyProvider { get; set; }
}

+public readonly struct ErrorMessageKeyContext
+{
+    public ValidationAttribute Attribute { get; init; }
+    public Type? DeclaringType { get; init; }
+    public string DisplayName { get; init; }
+    public string MemberName { get; init; }
+}
```

### API details

**`ValidationOptions.LocalizerProvider`** — Controls which `IStringLocalizer` is used for a given declaring type. The delegate receives the declaring type and an `IStringLocalizerFactory`, and returns the `IStringLocalizer` to use. When `null` (the default), localization is disabled and validation messages use the default `DataAnnotations` behavior. Typical configurations: per-type lookup via `(type, factory) => factory.Create(type)`, or shared resource via `(_, factory) => factory.Create(typeof(SharedResource))`.

**`ValidationOptions.ErrorMessageKeyProvider`** — Controls the resource key used for localization lookup. Called as a fallback when an attribute does not have an explicit `ErrorMessage` set, enabling convention-based key selection (e.g., `context => $"{context.Attribute.GetType().Name}_Error"`). When `null` (the default), only attributes with `ErrorMessage` set are localized. When both `ErrorMessage` and `ErrorMessageKeyProvider` are available, `ErrorMessage` takes precedence. This addresses a long-standing request to localize built-in attribute messages without modifying model classes:
- [dotnet/aspnetcore#4848](https://github.com/dotnet/aspnetcore/issues/4848) — Localize default validation error messages without specifying `ErrorMessage` on every attribute
- [dotnet/aspnetcore#33073](https://github.com/dotnet/aspnetcore/issues/33073) — Global localization of DataAnnotations validation messages
- [dotnet/runtime#24084](https://github.com/dotnet/runtime/issues/24084) — Localization of default `ValidationAttribute` error messages

**`ErrorMessageKeyContext`** — Context struct passed to the `ErrorMessageKeyProvider` delegate. Contains the `ValidationAttribute` instance, the declaring type (or `null` for top-level parameter validation), the resolved display name, and the member name.

**`IValidationAttributeFormatter`** — Formats a localized error message template with attribute-specific arguments. This replaces MVC's adapter system with a simpler, framework-independent interface. For example, `RangeAttribute`'s template `"The {0} field must be between {1} and {2}."` requires the min/max values as `{1}` and `{2}`. The formatter receives the culture, the localized template, and the display name, and returns the fully formatted message.

**`ValidationAttributeFormatterRegistry`** — Keyed registry mapping attribute types to formatter factories. Resolution order: (1) if the attribute implements `IValidationAttributeFormatter` itself (self-formatting), it is returned directly; (2) if a factory is registered for the attribute type, it creates a formatter; (3) `null` is returned — the pipeline falls back to formatting with only the display name.

## Usage Examples

### Localizer configuration

**Enable localization with per-type resource files (default):**

```csharp
builder.Services.AddLocalization();
builder.Services.AddValidation();
```

**Use a shared resource file:**

```csharp
builder.Services.AddLocalization();
builder.Services.AddValidation(options =>
{
    options.LocalizerProvider = (_, factory) => factory.Create(typeof(ValidationMessages));
});
```

**Use a custom IStringLocalizer:**

```csharp
builder.Services.AddSingleton<IStringLocalizerFactory, JsonStringLocalizerFactory>()
builder.Services.AddValidation();
```

### Attribute-specific formatting

**Self-formatting custom attribute:**

```csharp
class CustomRangeAttribute : ValidationAttribute, IValidationAttributeFormatter
{
    public int Min { get; }
    public int Max { get; }

    public string FormatErrorMessage(CultureInfo culture, string messageTemplate, string displayName)
        => string.Format(culture, messageTemplate, displayName, Min, Max);
}
```

**External formatter registration (when you don't control the attribute source):**

```csharp
builder.Services.AddValidation(options =>
{
    options.AttributeFormatters.AddFormatter<CustomAttribute>(a => new CustomAttributeFormatter(a))
});
```

### Programmatic message keys

**Convention-based key selection (localize built-in attributes without explicit `ErrorMessage`):**

```csharp
builder.Services.AddValidation(options =>
{
    options.ErrorMessageKeyProvider = context =>
        $"{context.Attribute.GetType().Name}_Error";
});

// Now [Required] looks up "RequiredAttribute_Error" in the resource file,
// without needing [Required(ErrorMessage = "RequiredAttribute_Error")] on every property.
```

## Alternative Designs

**`LocalizerProvider` placement — core package vs separate localization package.** The current proposal places `LocalizerProvider` directly on `ValidationOptions` in the core `Microsoft.Extensions.Validation` package. An alternative is to put it in a separate `ValidationLocalizationOptions` class in a new `Microsoft.Extensions.Validation.Localization` package, which would avoid the core package taking a dependency on `Microsoft.Extensions.Localization.Abstractions`. The direct placement was chosen for simplicity — a single `AddValidation(options => ...)` call configures everything.

**Named delegate types vs `Func<>`.** Named delegates (e.g., `delegate string? ErrorMessageKeyProvider(in ErrorMessageKeyContext context)`) could provide better readability and support `in` (pass-by-reference) parameters for the `ErrorMessageKeyContext` struct. `Func<>` was chosen for simplicity, following the [framework design guidelines](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/events-and-callbacks).

**`ValidationAttributeFormatterRegistry` vs DI-based formatter resolution.** An alternative is to register formatters via DI (e.g., `services.AddSingleton<IValidationAttributeFormatter<RangeAttribute>, RangeAttributeFormatter>()`). The registry approach was chosen because formatters are attribute-instance-specific — they need access to the attribute's property values (e.g., `RangeAttribute.Minimum`) — so a factory pattern (`Func<TAttribute, IValidationAttributeFormatter>`) is a natural fit. A keyed registry on `ValidationOptions` keeps this self-contained without introducing a generic DI service resolution pattern.

## Risks

- **`LocalizerProvider` ties core package to `IStringLocalizer` types.** The `Func<Type, IStringLocalizerFactory, IStringLocalizer>` signature introduces a reference to `Microsoft.Extensions.Localization.Abstractions` in the core `Microsoft.Extensions.Validation` package. This could be avoided by placing `LocalizerProvider` in a separate options class in a localization-specific package, at the cost of more complex setup.
- **No localization for `IValidatableObject` results.** Messages from `IValidatableObject.Validate` are expected to be localized by the implementation itself (it can resolve `IStringLocalizer` via `ValidationContext`). The localization pipeline does not post-process these messages.
