using FluentLocalizer;
using FluentLocalizer.Store.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

var httpClient = new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
};
builder.Services.AddSingleton(httpClient);

var store = new HttpJsonStore(httpClient, new HttpJsonStoreOptions
{
    ResourcesPath = "locales",
    FallbackCulture = "en-US",
    ThrowOnMissingStore = true
});

builder.Services.AddSingleton(store);
builder.Services.AddFluentLocalizer(options =>
{
    options.MissingKeyBehavior = MissingTranslationBehavior.ReturnConfiguredValue;
    options.MissingKeyFallbackValue = "[missing:{key} in {culture}]";
})
.WithStore(store);

var app = builder.Build();

// Preload the base locale files. Without a manifest here, namespace files load on first use.
await store.LoadAsync(["en-US", "it-IT"]);

await app.RunAsync();
