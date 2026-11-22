using FluentLocalizer.Store.Json;
using System.Globalization;
using System.Net;
using System.Net.Http;

namespace FluentLocalizer.Test;

public sealed class NeutralMappingsTests
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    private const string MappedFile = "custom/catalog.json";
    private const string ExactCatalog = "{\"Exact\":\"esatto\"}";
    private const string MappedCatalog = "{\"Extra\":\"Italian extra\"}";

    [Fact]
    public void File_store_checks_neutral_mapping_after_exact_catalog()
    {
        using var locales = new TemporaryLocaleDirectory();
        locales.Write("it-IT.json", ExactCatalog);
        locales.Write(MappedFile, MappedCatalog);
        var options = new JsonFileStoreOptions { ResourcesPath = locales.Path };
        options.FileMappings["it"] = MappedFile;
        using var store = new JsonFileStore(options);

        Assert.Equal("esatto", store.GetTemplate("Exact", Italian));
        Assert.Equal("Italian extra", store.GetTemplate("Extra", Italian));
    }

    [Fact]
    public void Embedded_store_checks_neutral_mapping_during_regional_lookup()
    {
        var options = new EmbeddedJsonStoreOptions
        {
            ResourcesPath = "EmbeddedLocales/Namespaces",
            ResourceAssembly = typeof(NeutralMappingsTests).Assembly
        };
        options.FileMappings["it"] = "it.Extra.json";
        var store = new EmbeddedJsonStore(options);

        Assert.Equal("Italian extra", store.GetTemplate("Extra", Italian));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Http_preload_and_lookup_use_the_neutral_mapping(bool useManifest)
    {
        using var client = new HttpClient(new CatalogHandler(useManifest)) { BaseAddress = new Uri("https://example.test/") };
        var options = new HttpJsonStoreOptions();
        options.FileMappings["it"] = MappedFile;
        using var store = new HttpJsonStore(client, options);
        var token = TestContext.Current.CancellationToken;

        await store.LoadAsync(Italian, token);
        Assert.Equal("esatto", store.GetTemplate("Exact", Italian));
        Assert.Equal("Italian extra", store.GetTemplate("Extra", Italian));
        Assert.Equal("Italian extra", await store.GetTemplateAsync("Extra", Italian, token));
    }

    private sealed class CatalogHandler(bool useManifest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/locales/manifest.json" when useManifest => "[\"it-IT.json\",\"custom/catalog.json\"]",
                "/locales/it-IT.json" => ExactCatalog,
                "/locales/custom/catalog.json" => MappedCatalog,
                _ => null
            };

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
