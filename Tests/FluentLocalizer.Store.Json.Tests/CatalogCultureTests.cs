using FluentLocalizer.Store.Json;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;

namespace FluentLocalizer.Test;

public sealed class CatalogCultureTests
{
    private const string Catalog = """
        { "FallbackGrammar": "{quantity, plural, one {# item} other {# items}} | {quantity, selectordinal, one {#st} two {#nd} few {#rd} other {#th}} | {amount, number} | {date, date, short}" }
        """;

    [Fact]
    public void The_culture_key_of_an_explicit_mapping_identifies_the_catalog()
    {
        using var locales = new TemporaryLocaleDirectory();
        locales.Write("pt-BR.json", Catalog);
        var options = new JsonFileStoreOptions { ResourcesPath = locales.Path };
        options.FileMappings["pt-PT"] = "pt-BR.json";
        using var store = new JsonFileStore(options);

        var result = ((ITranslationStoreWithCulture)store).GetTemplateWithCulture("FallbackGrammar", CultureInfo.GetCultureInfo("pt-PT"));

        Assert.NotNull(result.Template);
        Assert.Equal("pt-PT", result.Culture?.Name);
    }

    [Fact]
    public async Task Built_in_stores_use_catalog_grammar_and_request_number_and_date_formats()
    {
        using var locales = new TemporaryLocaleDirectory();
        locales.Write("en-US.json", Catalog);
        using var file = new JsonFileStore(new JsonFileStoreOptions { ResourcesPath = locales.Path });
        var embedded = new EmbeddedJsonStore(new EmbeddedJsonStoreOptions
        {
            ResourceAssembly = typeof(CatalogCultureTests).Assembly,
            LoadOnDemand = true
        });
        using var client = new HttpClient(new EnglishCatalogHandler()) { BaseAddress = new Uri("https://example.test/") };
        using var http = new HttpJsonStore(client);

        #pragma warning disable CS0618 // Check that the compatibility wrapper preserves catalog metadata.
        using var legacy = new JsonStore(new JsonStoreOptions
        {
            SearchMode = JsonStoreLocation.FileSystem,
            ResourcesPath = locales.Path
        });
        #pragma warning restore CS0618

        var requested = new CultureInfo("ru-RU");
        requested.NumberFormat.NumberGroupSeparator = "_";
        var date = new DateTime(2026, 1, 13);
        const decimal amount = 1234.5m;
        var expected = $"21 items | 21st | {amount.ToString("N1", requested)} | {date.ToString("d", requested)}";
        CancellationToken token = TestContext.Current.CancellationToken;

        foreach (var store in new ITranslationStore[] { file, embedded, http, legacy })
        {
            var request = new Translator(store).Get("FallbackGrammar").WithCulture(requested)
                .Pluralize(21).WithArg("amount", amount).WithArg("date", date);

            Assert.Equal(expected, await request.ResolveAsync(token));
            Assert.Equal(expected, request.Resolve());
        }
    }

    private sealed class EnglishCatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = request.RequestUri!.AbsolutePath.EndsWith("/en-US.json", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Catalog, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }
    }
}
