// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Localization;

namespace Microsoft.Extensions.Validation;

/// <summary>
/// Provides localization services for the validation pipeline. Resolves localized display names
/// and error messages using <see cref="IStringLocalizer"/> based on the configuration in
/// <see cref="ValidationOptions"/>.
/// </summary>
[Experimental("ASP0029", UrlFormat = "https://aka.ms/aspnet/analyzer/{0}")]
public sealed class ValidationLocalizer
{
    private readonly IStringLocalizerFactory _factory;
    private readonly Func<Type, IStringLocalizerFactory, IStringLocalizer>? _localizerProvider;
    private readonly Func<ErrorMessageKeyContext, string?>? _keyProvider;
    private readonly ValidationAttributeFormatterRegistry _formatters;
    private readonly ConcurrentDictionary<Type, IStringLocalizer> _localizerCache = new();

    /// <summary>
    /// Initializes a new instance of <see cref="ValidationLocalizer"/> using the specified
    /// <see cref="IStringLocalizerFactory"/> and <see cref="ValidationOptions"/>.
    /// </summary>
    /// <param name="factory">The string localizer factory to use for creating localizers.</param>
    /// <param name="options">The validation options containing localization configuration.</param>
    public ValidationLocalizer(IStringLocalizerFactory factory, ValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);

        _factory = factory;
        _localizerProvider = options.LocalizerProvider;
        _keyProvider = options.ErrorMessageKeyProvider;
        _formatters = options.AttributeFormatters;
    }

    /// <summary>
    /// Resolves a localized display name. Returns the original <paramref name="displayName"/>
    /// if no localized value is found.
    /// </summary>
    /// <param name="displayName">The display name to localize (typically from <see cref="DisplayAttribute.Name"/>).</param>
    /// <param name="declaringType">The type that declares the member, or <see langword="null"/> for parameters.</param>
    /// <returns>The localized display name, or the original value if not found.</returns>
    public string ResolveDisplayName(string displayName, Type? declaringType)
    {
        var localizer = GetLocalizer(declaringType);
        var localizedName = localizer[displayName];

        return localizedName.ResourceNotFound ? displayName : localizedName.Value;
    }

    /// <summary>
    /// Resolves a localized, fully formatted error message for a validation attribute.
    /// Returns <see langword="null"/> if the attribute uses its own resource-based localization
    /// (<see cref="ValidationAttribute.ErrorMessageResourceType"/> is set),
    /// or no localized value is found.
    /// </summary>
    /// <param name="attribute">The validation attribute that produced the error.</param>
    /// <param name="displayName">The (possibly localized) display name of the member.</param>
    /// <param name="declaringType">The type that declares the member, or <see langword="null"/> for parameters.</param>
    /// <returns>The localized error message, or <see langword="null"/> to use the attribute's default message.</returns>
    public string? ResolveErrorMessage(ValidationAttribute attribute, string displayName, Type? declaringType)
    {
        // Skip localization when the attribute already handles its own via ResourceType.
        if (attribute.ErrorMessageResourceType is not null)
        {
            return null;
        }

        var lookupKey = !string.IsNullOrEmpty(attribute.ErrorMessage)
            ? attribute.ErrorMessage
            : _keyProvider?.Invoke(new ErrorMessageKeyContext
            {
                Attribute = attribute,
                DisplayName = displayName,
                DeclaringType = declaringType
            });

        if (lookupKey is null)
        {
            return null;
        }

        var localizer = GetLocalizer(declaringType);
        var localizedTemplate = localizer[lookupKey];

        if (localizedTemplate.ResourceNotFound)
        {
            return null;
        }

        var attributeFormatter = _formatters.GetFormatter(attribute);

        return attributeFormatter?.FormatErrorMessage(CultureInfo.CurrentCulture, localizedTemplate, displayName)
            ?? string.Format(CultureInfo.CurrentCulture, localizedTemplate, displayName);
    }

    private IStringLocalizer GetLocalizer(Type? type)
    {
        var resourceSource = type ?? typeof(object);

        return _localizerCache.GetOrAdd(resourceSource, _localizerProvider is not null
            ? t => _localizerProvider(t, _factory)
            : _factory.Create);
    }
}
