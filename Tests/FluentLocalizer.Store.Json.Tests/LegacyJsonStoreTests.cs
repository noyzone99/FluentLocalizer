using FluentLocalizer.Store.Json;
using System.Globalization;

namespace FluentLocalizer.Test;

#pragma warning disable CS0618 // The legacy store remains supported for existing callers.
public sealed class LegacyJsonStoreTests
{
    [Fact]
    public void Forwards_the_file_catalog_size_limit()
    {
        using var locales = new TemporaryLocaleDirectory();
        locales.Write("en-US.json", "{\"Hello\":\"A longer translation\"}");
        using var store = new JsonStore(new JsonStoreOptions
        {
            SearchMode = JsonStoreLocation.FileSystem,
            ResourcesPath = locales.Path,
            MaxDocumentBytes = 8
        });

        Assert.Throws<InvalidDataException>(() => store.GetTemplate("Hello", CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void Forwards_the_embedded_catalog_size_limit()
    {
        Assert.Throws<InvalidDataException>(() => new JsonStore(new JsonStoreOptions
        {
            SearchMode = JsonStoreLocation.EmbeddedResources,
            ResourceAssembly = typeof(LegacyJsonStoreTests).Assembly,
            MaxDocumentBytes = 8
        }));
    }
}
