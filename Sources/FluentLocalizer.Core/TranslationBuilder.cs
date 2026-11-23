using FluentLocalizer.Logging;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FluentLocalizer;

/// <summary>
/// Builds and resolves a translation request by combining store access, culture, arguments, and formatting options.
/// </summary>
/// <param name="store">The store used to retrieve the template. Cannot be <see langword="null"/>.</param>
/// <param name="key">The key to resolve. Cannot be <see langword="null"/>.</param>
/// <param name="options">The missing-key and formatting policies to use; defaults to a new <see cref="TranslationOptions"/>.</param>
/// <param name="culture">The culture used for lookup and formatting; defaults to <see cref="CultureInfo.CurrentUICulture"/>.</param>
/// <param name="logger">An optional logger for resolution events.</param>
/// <exception cref="ArgumentNullException">The <paramref name="store"/> or <paramref name="key"/> is <see langword="null"/>.</exception>
public class TranslationBuilder(ITranslationStore store, string key, TranslationOptions? options = null, CultureInfo? culture = null, ITranslationLogger? logger = null)
{
    private const string genderDefaultKey = "gender";
    private const string quantityDefaultKey = "quantity";
    private static readonly Regex _fallbackTokens = new(@"\{(?:(?<key>[kK][eE][yY]|0)|[cC][uU][lL][tT][uU][rR][eE]|1)\}");

    private readonly ITranslationStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly string _key = key ?? throw new ArgumentNullException(nameof(key));
    private readonly ITranslationLogger? _logger = logger;
    private TranslationOptions _options = options ?? new TranslationOptions();
    private CultureInfo _culture = culture ?? CultureInfo.CurrentUICulture;
    private readonly Dictionary<string, object?> _arguments =
        new(StringComparer.OrdinalIgnoreCase) { { genderDefaultKey, nameof(Gender.Unspecified).ToLowerInvariant() }, { quantityDefaultKey, null } };
    private LetterCase _case = LetterCase.AsIs;

    #region Fluent API
    /// <summary>
    /// Sets the culture used to resolve the translation template.
    /// </summary>
    /// <param name="culture">The culture to use.</param>
    /// <returns>The current builder instance to allow fluent composition.</returns>
    /// <remarks>Passing <see langword="null"/> resets the culture to <see cref="CultureInfo.CurrentCulture"/>.</remarks>
    public TranslationBuilder WithCulture(CultureInfo culture)
    {
        _culture = culture ?? CultureInfo.CurrentCulture;
        return this;
    }

    /// <summary>
    /// Sets the culture used to resolve the translation template from a culture name.
    /// </summary>
    /// <param name="cultureName">The culture name to resolve, such as <c>en-US</c>.</param>
    /// <returns>The current builder instance to allow fluent composition.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cultureName"/> is <see langword="null"/>.</exception>
    /// <exception cref="CultureNotFoundException">The supplied culture name is not recognized.</exception>
    public TranslationBuilder WithCulture(string cultureName)
    {
        _culture = CultureInfo.GetCultureInfo(cultureName);
        return this;
    }

    /// <summary>
    /// Replaces the builder options with a new configuration object.
    /// </summary>
    /// <param name="options">The configuration used for missing key and formatting behaviors.</param>
    /// <returns>The current builder instance to allow fluent composition.</returns>
    /// <remarks>Passing <see langword="null"/> resets the options to a new <see cref="TranslationOptions"/> instance.</remarks>
    public TranslationBuilder WithOptions(TranslationOptions options)
    {
        _options = options ?? new TranslationOptions();
        return this;
    }

    /// <summary>
    /// Supplies the quantity used for pluralization in message formatting.
    /// </summary>
    /// <param name="quantity">The quantity value to expose as the <c>quantity</c> argument.</param>
    /// <returns>The current builder instance to allow fluent composition.</returns>
    public TranslationBuilder Pluralize(int quantity) => WithArg(quantityDefaultKey, quantity);

    /// <summary>
    /// Supplies the gender used for gender-aware message formatting.
    /// </summary>
    /// <param name="gender">The gender value to expose as the <c>gender</c> argument.</param>
    /// <returns>The current builder instance to allow fluent composition.</returns>
    public TranslationBuilder Genderize(Gender gender) => WithArg(genderDefaultKey, gender.ToString().ToLowerInvariant());

    /// <summary>
    /// Adds or replaces a named runtime argument for the template.
    /// </summary>
    /// <param name="name">The argument name.</param>
    /// <param name="value">The argument value to pass to the formatter.</param>
    /// <returns>The current builder instance to allow fluent composition.</returns>
    /// <exception cref="ArgumentNullException">The <paramref name="name"/> is <see langword="null"/>.</exception>
    public TranslationBuilder WithArg(string name, object? value)
    {
        _arguments[name] = value;
        return this;
    }

    /// <summary>
    /// Adds a set of runtime arguments to the current builder.
    /// </summary>
    /// <param name="args">A dictionary of argument names and values to merge into the current request.</param>
    /// <returns>The current builder instance to allow fluent composition.</returns>
    /// <remarks>Arguments are matched without regard to case. A runtime argument replaces a value with the same name.</remarks>
    public TranslationBuilder WithArgs(Dictionary<string, object?> args)
    {
        if (args != null)
        {
            foreach (var arg in args)
            {
                WithArg(arg.Key, arg.Value);
            }
        }
        return this;
    }

    /// <summary>
    /// Applies a text transformation to the formatted message.
    /// </summary>
    /// <param name="letterCase">The casing strategy to apply after formatting.</param>
    /// <returns>The current builder instance to allow fluent composition.</returns>
    public TranslationBuilder WithCase(LetterCase letterCase)
    {
        _case = letterCase;
        return this;
    }

    /// <summary>
    /// Resolves the translation template asynchronously.
    /// </summary>
    /// <param name="cancellationToken">A token that can cancel the asynchronous store operation.</param>
    /// <returns>The resolved message or a fallback value based on the configured behavior.</returns>
    /// <exception cref="TranslationException">
    /// Thrown when the configured behavior is to throw for missing keys or formatting errors.
    /// </exception>
    /// <exception cref="OperationCanceledException">The <paramref name="cancellationToken"/> was canceled while the store was loading a template.</exception>
    /// <remarks>Exceptions raised by the store or by a configured exception factory are propagated.</remarks>
    public async Task<string> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var culture = _culture ?? CultureInfo.CurrentUICulture;
        LogDebug($"Resolving translation request for key '{_key}' for culture '{culture.Name}'.");

        (string? Template, CultureInfo? Culture) result;
        try
        {
            result = _store is ITranslationStoreWithCulture localized
                ? await localized.GetTemplateWithCultureAsync(_key, culture, cancellationToken).ConfigureAwait(false)
                : (await _store.GetTemplateAsync(_key, culture, cancellationToken).ConfigureAwait(false), null);
        }
        catch (KeyNotFoundException)
        {
            return HandleMissingTemplate();
        }
        catch (Exception ex) when (ex is not TranslationException)
        {
            LogError($"An unexpected exception occurred while resolving translation key '{_key}'.", ex);
            throw;
        }

        LogDebug($"Retrieved translation template for key '{_key}' for culture '{culture.Name}'.");
        return ResolveTemplate(result.Template, culture, result.Culture);
    }

    /// <summary>
    /// Resolves the translation template synchronously.
    /// </summary>
    /// <returns>The resolved message or a fallback value based on the configured behavior.</returns>
    /// <exception cref="TranslationException">
    /// Thrown when the configured behavior is to throw for missing keys or formatting errors.
    /// </exception>
    /// <exception cref="InvalidOperationException">The store cannot perform a synchronous lookup in its current state.</exception>
    /// <remarks>Exceptions raised by the store or by a configured exception factory are propagated.</remarks>
    public string Resolve()
    {
        var culture = _culture ?? CultureInfo.CurrentUICulture;
        LogDebug($"Resolving translation request for key '{_key}' for culture '{culture.Name}'.");

        (string? Template, CultureInfo? Culture) result;
        try
        {
            result = _store is ITranslationStoreWithCulture localized
                ? localized.GetTemplateWithCulture(_key, culture)
                : (_store.GetTemplate(_key, culture), null);
        }
        catch (KeyNotFoundException)
        {
            return HandleMissingTemplate();
        }
        catch (Exception ex) when (ex is not TranslationException)
        {
            LogError($"An unexpected exception occurred while resolving translation key '{_key}'.", ex);
            throw;
        }

        return ResolveTemplate(result.Template, culture, result.Culture);
    }

    #endregion Fluent API

    /// <summary>
    /// Formats a raw template with the current arguments and applies the configured case transformation.
    /// </summary>
    /// <param name="rawTemplate">The template content retrieved from the store.</param>
    /// <param name="requestedCulture">The culture used for number and date formatting.</param>
    /// <param name="catalogCulture">The culture from which the template was loaded, if known.</param>
    /// <returns>The formatted message, or a fallback value if the template is missing or malformed.</returns>
    private string ResolveTemplate(string? rawTemplate, CultureInfo requestedCulture, CultureInfo? catalogCulture)
    {
        LogDebug($"Formatting translation template for key '{_key}' for culture '{_culture?.Name ?? "unknown"}'.");

        if (string.IsNullOrWhiteSpace(rawTemplate))
        {
            return HandleMissingTemplate();
        }

        try
        {
            var argumentSet = _options.CreateArgumentSet(_arguments);
            var template = rawTemplate ?? string.Empty;

            if (template.Contains("{Length}") && !argumentSet.ContainsKey("Length"))
            {
                Dictionary<string, object?> lengthArguments = argumentSet.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase
                );
                lengthArguments["Length"] = argumentSet.Values.Count(static value => value is not null);
                argumentSet = lengthArguments;
            }

            var formattingCulture = GetFormattingCulture(requestedCulture, catalogCulture);
            string formatted = TranslationFormatter.Format(template, argumentSet, formattingCulture);
            var result = ApplyCaseTransformation(formatted, _case);
            LogDebug($"Translation request completed for key '{_key}' for culture '{_culture?.Name ?? "unknown"}'.");
            return result;
        }
        catch (Exception ex) when (ex is not TranslationException)
        {
            return HandleFormattingException(ex);
        }
    }

    private static CultureInfo GetFormattingCulture(CultureInfo requested, CultureInfo? catalog)
    {
        if (catalog is null || string.Equals(requested.Name, catalog.Name, StringComparison.OrdinalIgnoreCase))
            return requested;

        // MessageFormat uses the culture name for plural rules and the format providers for numbers and dates.
        var culture = (CultureInfo)catalog.Clone();
        culture.NumberFormat = requested.NumberFormat;
        culture.DateTimeFormat = requested.DateTimeFormat;
        return culture;
    }

    /// <summary>
    /// Handles missing templates according to the configured missing key policy.
    /// </summary>
    /// <returns>The fallback string returned for the missing template.</returns>
    private string HandleMissingTemplate()
    {
        string message = $"Translation key '{_key}' could not be resolved for culture '{_culture?.Name ?? "unknown"}'.";

        return _options.MissingKeyBehavior switch
        {
            MissingTranslationBehavior.ThrowException => ThrowMissingTemplate(message),
            MissingTranslationBehavior.ReturnConfiguredValue => ReturnFallback(message, ReplaceFallbackTokens(_options.MissingKeyFallbackValue)),
            _ => ReturnFallback(message, $"[{_key}]")
        };
    }

    /// <summary>
    /// Handles formatting exceptions according to the configured formatting error policy.
    /// </summary>
    /// <param name="exception">The exception raised during message formatting.</param>
    /// <returns>The fallback string returned for the formatting failure.</returns>
    private string HandleFormattingException(Exception exception)
    {
        string message = $"Formatting failed for translation key '{_key}' for culture '{_culture?.Name ?? "unknown"}'.";

        return _options.FormattingErrorBehavior switch
        {
            FormattingErrorBehavior.ThrowException => ThrowFormattingException(message, exception),
            _ => ReturnFallback(message, ReplaceFallbackTokens(_options.FormattingErrorFallbackValue))
        };
    }

    /// <summary>
    /// Throws the configured missing-key exception after logging the failure.
    /// </summary>
    /// <param name="message">The log message describing the missing translation.</param>
    /// <returns>Never returns; always throws.</returns>
    private string ThrowMissingTemplate(string message)
    {
        var missingException = _options.CreateMissingKeyException(_key, _culture);
        LogError(message, missingException);
        throw missingException;
    }

    private string ReturnFallback(string message, string fallbackValue)
    {
        LogWarning($"{message} Returning fallback value '{fallbackValue}'.");
        return fallbackValue;
    }

    private string ThrowFormattingException(string message, Exception exception)
    {
        var formattingException = _options.CreateFormattingException(_key, _culture, exception);
        LogError(message, formattingException);
        throw formattingException;
    }

    private void LogDebug(string message) => _logger?.Log(TranslationLogLevel.Debug, message);

    private void LogWarning(string message, Exception? exception = null) => _logger?.Log(TranslationLogLevel.Warning, message, exception);

    private void LogError(string message, Exception? exception = null) => _logger?.Log(TranslationLogLevel.Error, message, exception);

    /// <summary>
    /// Replaces the placeholder tokens supported by the fallback values.
    /// </summary>
    /// <param name="fallbackValue">The fallback string that may contain token values.</param>
    /// <returns>A fallback string with the translation key and culture tokens resolved.</returns>
    private string ReplaceFallbackTokens(string fallbackValue) =>
        _fallbackTokens.Replace(fallbackValue, match => match.Groups["key"].Success ? _key : _culture.Name);

    /// <summary>
    /// Converts the formatted text to a string representation of the builder for implicit string conversion.
    /// </summary>
    /// <param name="builder">The builder instance to resolve.</param>
    /// <returns>The resolved string value of the builder.</returns>
    public static implicit operator string(TranslationBuilder builder) => builder.Resolve();

    /// <summary>
    /// Returns the resolved translation as a string.
    /// </summary>
    /// <returns>The resolved translation message.</returns>
    public override string ToString() => Resolve();

    /// <summary>
    /// Applies the requested case transformation to the formatted text.
    /// </summary>
    /// <param name="text">The formatted text to transform.</param>
    /// <param name="letterCase">The requested casing strategy.</param>
    /// <returns>The transformed text, or the original string when the transformation is not applicable.</returns>
    private static string ApplyCaseTransformation(string text, LetterCase letterCase)
    {
        if (string.IsNullOrEmpty(text)) return text;

        return letterCase switch
        {
            LetterCase.Upper => text.ToUpperInvariant(),
            LetterCase.Lower => text.ToLowerInvariant(),
            LetterCase.CamelCase => ToCamelCase(text),
            LetterCase.PascalCase => ToPascalCase(text),
            LetterCase.SnakeCase => ToJoinedCase(text, '_'),
            LetterCase.KebabCase => ToJoinedCase(text, '-'),
            _ => text
        };
    }

    /// <summary>
    /// Converts text to camel case by splitting it into words and normalizing each piece.
    /// </summary>
    /// <param name="text">The text to convert.</param>
    /// <returns>The camel-cased representation of the input text.</returns>
    private static string ToCamelCase(string text)
    {
        var words = SplitWords(text);
        if (words.Count == 0)
        {
            return text;
        }

        StringBuilder builder = new();
        for (int index = 0; index < words.Count; index++)
        {
            var word = words[index];
            var first = StringInfo.GetNextTextElement(word);
            builder.Append(index == 0 ? first.ToLowerInvariant() : first.ToUpperInvariant());
            builder.Append(word[first.Length..].ToLowerInvariant());
        }

        return builder.ToString();
    }

    /// <summary>
    /// Converts text to PascalCase by splitting it into words and capitalizing each piece.
    /// </summary>
    /// <param name="text">The text to convert.</param>
    /// <returns>The PascalCase representation of the input text.</returns>
    private static string ToPascalCase(string text)
    {
        var words = SplitWords(text);
        if (words.Count == 0)
        {
            return text;
        }

        StringBuilder builder = new();
        foreach (var word in words)
        {
            var first = StringInfo.GetNextTextElement(word);
            builder.Append(first.ToUpperInvariant());
            builder.Append(word[first.Length..].ToLowerInvariant());
        }

        return builder.ToString();
    }

    /// <summary>
    /// Joins the words of the input text using a separator and lowercases each word.
    /// </summary>
    /// <param name="text">The text to split and join.</param>
    /// <param name="separator">The separator used between words.</param>
    /// <returns>The joined representation of the text.</returns>
    private static string ToJoinedCase(string text, char separator)
    {
        var words = SplitWords(text);
        if (words.Count == 0)
        {
            return text;
        }

        return string.Join(separator.ToString(), words.Select(word => word.ToLowerInvariant()));
    }

    /// <summary>
    /// Splits a text value into a list of words based on letter, digit, and casing boundaries.
    /// </summary>
    /// <param name="text">The text to analyze.</param>
    /// <returns>A list of words representing the meaningful chunks of the input.</returns>
    private static List<string> SplitWords(string text)
    {
        List<string> words = [];
        var elementStarts = StringInfo.ParseCombiningCharacters(text);
        int wordStart = -1;
        var previousCategory = UnicodeCategory.OtherNotAssigned;

        for (int element = 0; element < elementStarts.Length; element++)
        {
            int index = elementStarts[element];
            var category = CharUnicodeInfo.GetUnicodeCategory(text, index);
            if (!IsLetter(category) && category != UnicodeCategory.DecimalDigitNumber)
            {
                if (wordStart >= 0)
                {
                    words.Add(text[wordStart..index]);
                    wordStart = -1;
                }

                continue;
            }

            var nextCategory = element + 1 < elementStarts.Length
                ? CharUnicodeInfo.GetUnicodeCategory(text, elementStarts[element + 1])
                : UnicodeCategory.OtherNotAssigned;
            if (wordStart < 0)
            {
                wordStart = index;
            }
            else if (IsWordBoundary(previousCategory, category, nextCategory))
            {
                words.Add(text[wordStart..index]);
                wordStart = index;
            }

            previousCategory = category;
        }

        if (wordStart >= 0)
        {
            words.Add(text[wordStart..]);
        }

        return words;
    }

    /// <summary>
    /// Determines whether the transition between two text elements should be treated as a word boundary.
    /// </summary>
    /// <param name="previous">The category of the previous text element.</param>
    /// <param name="current">The category of the current text element.</param>
    /// <param name="next">The category of the next text element.</param>
    /// <returns><c>true</c> when the elements indicate a new word should start.</returns>
    private static bool IsWordBoundary(UnicodeCategory previous, UnicodeCategory current, UnicodeCategory next)
    {
        if (current == UnicodeCategory.UppercaseLetter &&
            (previous == UnicodeCategory.LowercaseLetter ||
             previous == UnicodeCategory.UppercaseLetter && next == UnicodeCategory.LowercaseLetter))
        {
            return true;
        }

        return (previous == UnicodeCategory.DecimalDigitNumber) != (current == UnicodeCategory.DecimalDigitNumber);
    }

    private static bool IsLetter(UnicodeCategory category) => category is UnicodeCategory.UppercaseLetter
        or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
        or UnicodeCategory.OtherLetter;
}
