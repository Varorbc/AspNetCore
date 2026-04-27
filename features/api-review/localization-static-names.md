# Improve static display name localization in Microsoft.Extensions.Validation

Issue: https://github.com/dotnet/aspnetcore/issues/65647

Implementation PR: https://github.com/dotnet/aspnetcore/pull/65648

API changes:

*REMOVED*Microsoft.Extensions.Validation.ValidatableParameterInfo.ValidatableParameterInfo(System.Type! parameterType, string! name, string! displayName) -> void
*REMOVED*Microsoft.Extensions.Validation.ValidatablePropertyInfo.ValidatablePropertyInfo(System.Type! declaringType, System.Type! propertyType, string! name, string! displayName) -> void
Microsoft.Extensions.Validation.ValidatableParameterInfo.ValidatableParameterInfo(System.Type! parameterType, string! name) -> void
Microsoft.Extensions.Validation.ValidatablePropertyInfo.ValidatablePropertyInfo(System.Type! declaringType, System.Type! propertyType, string! name) -> void
abstract Microsoft.Extensions.Validation.ValidatableParameterInfo.GetDisplayName() -> string?
abstract Microsoft.Extensions.Validation.ValidatablePropertyInfo.GetDisplayName() -> string?
abstract Microsoft.Extensions.Validation.ValidatableTypeInfo.GetDisplayName() -> string?

It is important to note that even though this involves breaking changes to released public types, those types are Experimental and are not expected to be widely used by anyone else than the M.E.V package itself.

---

## Background and Motivation

.NET offers three ways to specify user-visible display names on types, properties, and parameters:

1. **`DisplayAttribute` with a literal name** — `[Display(Name = "Product Name")]`
2. **`DisplayAttribute` with resource-based localized name** — `[Display(Name = nameof(Resources.ProductName), ResourceType = typeof(Resources))]`, where `DisplayAttribute.GetName()` resolves the localized string at runtime via a public static property on the resource type.
3. **`DisplayNameAttribute`** — `[DisplayName("Product Name")]`, a simpler attribute from `System.ComponentModel` without localization support.

`Microsoft.Extensions.Validation` currently only supports scenario 1 — `DisplayAttribute.Name` as a literal string. It does **not** support:

- **`DisplayAttribute` with `ResourceType`** — The `ResourceType` property is ignored, so the `Name` value is used as a literal string instead of being resolved as a resource key. Validation error messages contain the resource key instead of the localized value.
- **`DisplayNameAttribute`** — Completely ignored.

Both MVC (`DataAnnotationsMetadataProvider`) and Blazor (`FieldIdentifier`/`ExpressionMemberAccessor`) already resolve display names with the following precedence: `DisplayAttribute.GetName()` → `DisplayNameAttribute.DisplayName` → member name. Users should be able to reuse their existing model classes when adopting `Microsoft.Extensions.Validation` without losing display name functionality.

See [dotnet/aspnetcore#65647](https://github.com/dotnet/aspnetcore/issues/65647) for the full issue.

## Proposed API

```diff
namespace Microsoft.Extensions.Validation;

[Experimental("ASP0029")]
public abstract class ValidatablePropertyInfo
{
-    protected ValidatablePropertyInfo(Type declaringType, Type propertyType, string name, string displayName);
+    protected ValidatablePropertyInfo(Type declaringType, Type propertyType, string name);
+    protected abstract string? GetDisplayName();
}

[Experimental("ASP0029")]
public abstract class ValidatableParameterInfo
{
-    protected ValidatableParameterInfo(Type parameterType, string name, string displayName);
+    protected ValidatableParameterInfo(Type parameterType, string name);
+    protected abstract string? GetDisplayName();
}

[Experimental("ASP0029")]
public abstract class ValidatableTypeInfo
{
+    protected abstract string? GetDisplayName();
}
```

The `displayName` constructor parameter is removed from `ValidatablePropertyInfo` and `ValidatableParameterInfo`. A new `protected abstract string? GetDisplayName()` method is added to all three base classes. `GetDisplayName()` returns `null` when no display attribute is present, so the absence of an explicit display name can be differentiated from a name that happens to equal the member name.

## Usage Examples

These API changes are consumed by the validation source generator. User-facing model code remains unchanged — existing display attribute patterns now work correctly:

```csharp
// Scenario 1: Literal display name (already worked)
public class Product
{
    [Display(Name = "Product Name")]
    [Required]
    public string Name { get; set; }
}

// Scenario 2: Resource-based localized display name (now works)
public class Product
{
    [Display(Name = nameof(Resources.ProductName), ResourceType = typeof(Resources))]
    [Required]
    public string Name { get; set; }
}

// Scenario 3: DisplayNameAttribute (now works)
public class Product
{
    [DisplayName("Product Name")]
    [Required]
    public string Name { get; set; }
}
```

The generated source-generator subclasses override `GetDisplayName()` to call cached runtime helpers that perform the attribute lookup:

```csharp
// Example of generated code (simplified)
private sealed class GeneratedValidatablePropertyInfo_Name : ValidatablePropertyInfo
{
    public GeneratedValidatablePropertyInfo_Name(/* ... */)
        : base(declaringType, propertyType, "Name") { }

    protected override string? GetDisplayName()
        => ValidationAttributeCache.GetPropertyDisplayName(typeof(Product), "Name");
}
```

## Alternative Designs

An alternative approach would be to source-generate direct property access at build time — for example, emitting `static () => Resources.ProductName` for the `ResourceType` scenario. However, this changes the behavior: if a resource key referenced by `DisplayAttribute.Name` does not exist on `ResourceType`, the runtime `DisplayAttribute.GetName()` throws `InvalidOperationException` at validation time, whereas the generated code would produce a **build error**. This is problematic during development when the developer may not yet have all resource keys defined — the validation source generator would cause noisy build errors for what is otherwise a non-blocking misconfiguration.

A hybrid approach was also considered: keep generating the string literal value for `[Display(Name = "...")]` and `[DisplayName("...")]`, and only use runtime attribute resolution for the `ResourceType` case. This was rejected in favor of the simpler, uniform runtime resolution approach that is consistent with how `ValidationAttribute` instances are already resolved and cached.

## Risks

- **Breaking change to `protected` constructors**: The `displayName` parameter is removed from the `ValidatablePropertyInfo` and `ValidatableParameterInfo` constructors. However, all three types are marked `[Experimental("ASP0029")]` and their constructors are `protected` — only the source-generated code and the internal `RuntimeValidatableParameterInfoResolver` call them. These types are not expected to be subclassed by users in typical validation scenarios.
- **New abstract method**: Adding `abstract GetDisplayName()` to existing abstract classes is a breaking change for any external subclass. The same experimental-API mitigating factor applies.
- **One-time reflection cost**: Display names are now resolved at runtime via `GetCustomAttribute()` calls instead of being baked as string literals. The cost is mitigated by one-time-per-member caching, consistent with the existing `ValidationAttribute` caching pattern, and is AOT-safe (`DisplayAttribute.ResourceType` carries `[DynamicallyAccessedMembers]`).
