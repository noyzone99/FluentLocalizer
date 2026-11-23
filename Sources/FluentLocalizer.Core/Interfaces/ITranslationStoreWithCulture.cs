using System.Globalization;

#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace FluentLocalizer;
#pragma warning restore IDE0130 // Namespace does not match folder structure

/// <summary>Allows a translation store to report the culture of the catalog that supplied a template.</summary>
/// <remarks>The translator uses the catalog culture for ICU plural and ordinal rules and the requested culture for number and date formatting.</remarks>
public interface ITranslationStoreWithCulture : ITranslationStore
{
    /// <summary>Returns a template together with the culture of the catalog where it was found.</summary>
    /// <param name="key">The translation key to retrieve.</param>
    /// <param name="culture">The requested culture.</param>
    /// <returns>The template and its catalog culture. A null template is a miss. A null catalog culture uses the requested culture.</returns>
    (string? Template, CultureInfo? Culture) GetTemplateWithCulture(string key, CultureInfo culture);

    /// <summary>Asynchronously returns a template together with the culture of the catalog where it was found.</summary>
    /// <param name="key">The translation key to retrieve.</param>
    /// <param name="culture">The requested culture.</param>
    /// <param name="cancellationToken">A token that can cancel the asynchronous operation.</param>
    /// <returns>The template and its catalog culture. A null template is a miss. A null catalog culture uses the requested culture.</returns>
    Task<(string? Template, CultureInfo? Culture)> GetTemplateWithCultureAsync(string key, CultureInfo culture, CancellationToken cancellationToken = default);
}
