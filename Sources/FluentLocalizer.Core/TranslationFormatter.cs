using System.Globalization;
using Jeffijoe.MessageFormat;

namespace FluentLocalizer;

internal static class TranslationFormatter
{
    private const int ReuseLimit = 256;
    private const int CacheableTemplateLength = 4096;

    [ThreadStatic] private static MessageFormatter? _formatter;
    [ThreadStatic] private static int _remainingUses;

    internal static string Format(string template, IReadOnlyDictionary<string, object?> arguments, CultureInfo? culture)
    {
        if (template.Length > CacheableTemplateLength)
            return new MessageFormatter(useCache: false).FormatMessage(template, arguments, culture);

        // MessageFormat caches parsed messages and branches inside each instance. Rotate it to limit retained patterns.
        if (_formatter is null || _remainingUses == 0)
        {
            _formatter = new MessageFormatter();
            _remainingUses = ReuseLimit;
        }

        _remainingUses--;
        return _formatter.FormatMessage(template, arguments, culture);
    }
}
