using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace FluentLocalizer.Store.Json;

/// <summary>Base class for JSON stores that share culture fallback and nested-key lookup.</summary>
public abstract class JsonTranslationStoreBase(JsonStoreSettings options) : ITranslationStore
{
    private readonly ConcurrentDictionary<string, JsonElement> _documents = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _templateCacheLock = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, string Value)>> _templateCache = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, string Value)> _templateCacheOrder = new();
    private long _templateCacheGeneration;
    // ponytail: cap retained strings at 512 entries; tune only if real workloads need a different bound.
    private const int TemplateCacheCapacity = 512;

    /// <summary>Gets the shared fallback and file mapping settings used by this store.</summary>
    protected JsonStoreSettings Options { get; } = options;

    /// <summary>Returns candidate JSON file names in culture and fallback order.</summary>
    protected IReadOnlyList<string> ResolveCandidates(CultureInfo culture) =>
        JsonStoreCore.ResolveCandidates(culture, Options.FallbackCulture, Options.FileMappings);

    /// <summary>Parses and caches a JSON object under its file name.</summary>
    /// <param name="name">The file name used during culture resolution.</param>
    /// <param name="json">The JSON document contents.</param>
    protected void SetDocument(string name, string json)
    {
        using var document = JsonDocument.Parse(json);
        SetDocument(name, document.RootElement);
    }

    /// <summary>Clones and caches a parsed JSON root object.</summary>
    /// <param name="name">The file name used during culture resolution.</param>
    /// <param name="root">The JSON root element.</param>
    protected void SetDocument(string name, JsonElement root) => _documents[name] = JsonStoreCore.RequireObjectRoot(root).Clone();

    /// <summary>Removes a cached JSON document.</summary>
    /// <param name="name">The cached file name.</param>
    protected void RemoveDocument(string name) => _documents.TryRemove(name, out _);

    /// <summary>Checks whether any candidate JSON document is cached for a culture.</summary>
    /// <param name="culture">The culture to check.</param>
    protected virtual bool HasCandidate(CultureInfo culture) =>
        ResolveCandidates(culture).Any(_documents.ContainsKey);

    /// <summary>Finds a template in cached JSON documents using the configured culture fallback order.</summary>
    /// <param name="key">The colon-separated JSON key.</param>
    /// <param name="culture">The requested culture.</param>
    /// <returns>The matching string value, or <see langword="null"/> when no value is found.</returns>
    protected virtual string? FindTemplate(string key, CultureInfo culture)
    {
        foreach (var candidate in ResolveCandidates(culture))
        {
            if (_documents.TryGetValue(candidate, out var root) && JsonStoreCore.TryGetValue(root, key, out var value))
                return value;
        }

        return null;
    }

    /// <inheritdoc />
    public virtual string? GetTemplate(string key, CultureInfo culture)
    {
        var cacheKey = culture.Name + "\0" + key;
        long generation;
        lock (_templateCacheLock)
        {
            if (_templateCache.TryGetValue(cacheKey, out var cached))
            {
                _templateCacheOrder.Remove(cached);
                _templateCacheOrder.AddFirst(cached);
                return cached.Value.Value;
            }
            generation = _templateCacheGeneration;
        }

        var result = FindTemplate(key, culture);
        if (result is null && Options.ThrowOnMissingStore && !HasCandidate(culture))
            throw new FileNotFoundException($"No translation files were found for culture '{culture.Name}' or fallback '{Options.FallbackCulture}'.");

        if (result is not null)
        {
            lock (_templateCacheLock)
            {
                // A reload can invalidate a result while FindTemplate is still running.
                if (generation != _templateCacheGeneration) return result;

                if (_templateCache.TryGetValue(cacheKey, out var existing))
                {
                    _templateCacheOrder.Remove(existing);
                    _templateCacheOrder.AddFirst(existing);
                }
                else
                {
                    var node = _templateCacheOrder.AddFirst((cacheKey, result));
                    _templateCache.Add(cacheKey, node);
                    if (_templateCache.Count > TemplateCacheCapacity)
                    {
                        var oldest = _templateCacheOrder.Last!;
                        _templateCacheOrder.RemoveLast();
                        _templateCache.Remove(oldest.Value.Key);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>Clears cached resolved templates after the underlying store changes.</summary>
    protected void ClearTemplateCache()
    {
        lock (_templateCacheLock)
        {
            _templateCacheGeneration++;
            _templateCache.Clear();
            _templateCacheOrder.Clear();
        }
    }

    /// <inheritdoc />
    public virtual Task<string?> GetTemplateAsync(string key, CultureInfo culture, CancellationToken cancellationToken = default) =>
        Task.FromResult(GetTemplate(key, culture));
}

internal readonly record struct JsonFileCandidate(string Path, string CultureName, bool IsNamespaceFile);

internal static class JsonStoreCore
{
    internal static JsonElement RequireObjectRoot(JsonElement root) => root.ValueKind == JsonValueKind.Object
        ? root
        : throw new JsonException("A translation catalog must contain a JSON object at the root.");

    internal static string? CultureFromPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Length == 2 && TryCulture(parts[0], out var folderCulture)) return folderCulture;
        if (parts.Length != 1 || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return null;

        var stem = parts[0][..^5];
        if (TryCulture(stem, out var wholeCulture)) return wholeCulture;
        var separator = stem.IndexOf('.');
        if (separator < 0) return null;
        var hasPrefix = TryCulture(stem[..separator], out var prefixCulture);
        var suffix = stem[(stem.LastIndexOf('.') + 1)..];
        if ((!hasPrefix || !prefixCulture.Contains('-') && suffix.Contains('-')) && TryCulture(suffix, out var suffixCulture))
            return suffixCulture;
        return hasPrefix ? prefixCulture : null;
    }

    private static bool TryCulture(string name, out string culture)
    {
        culture = string.Empty;
        try
        {
            culture = CultureInfo.GetCultureInfo(name).Name;
            return culture.Length > 0 && culture.Equals(name, StringComparison.OrdinalIgnoreCase);
        }
        catch (CultureNotFoundException) { return false; }
    }

    internal static string? SelectCulture(CultureInfo requested, IEnumerable<string> available)
    {
        var names = available.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Contains(requested.Name, StringComparer.OrdinalIgnoreCase)) return requested.Name;

        if (requested.IsNeutralCulture)
        {
            return names.Where(name => name.StartsWith(requested.Name + "-", StringComparison.OrdinalIgnoreCase))
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }

        var neutral = requested.Parent.Name;
        if (names.Contains(neutral, StringComparer.OrdinalIgnoreCase)) return neutral;
        if (names.Any(name => name.StartsWith(neutral + "-", StringComparison.OrdinalIgnoreCase)))
            throw new FileNotFoundException($"No translation files were found for culture '{requested.Name}'; another regional variant exists.");
        return null;
    }

    internal static string ValidateRelativeJsonPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.IndexOfAny(['?', '#', '\0']) >= 0)
            throw new ArgumentException("Translation paths must be relative JSON paths.", nameof(path));

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/')
            || normalized.Split('/').Any(static segment => segment is "" or "." or ".." || segment.Contains(':'))
            || !normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Translation paths must stay inside the configured resource path and end in .json.", nameof(path));
        }

        return normalized;
    }

    internal static List<string> ResolveCandidates(CultureInfo culture, string fallbackCulture, IDictionary<string, string> mappings)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return;
            var candidate = fileName!;
            if (seen.Add(candidate)) candidates.Add(candidate);
        }

        void AddCulture(CultureInfo candidate)
        {
            if (mappings.TryGetValue(candidate.Name, out var mapped)) Add(mapped);
            Add(candidate.Name + ".json");
            var neutral = candidate.TwoLetterISOLanguageName;
            if (mappings.TryGetValue(neutral, out var neutralMapped)) Add(neutralMapped);
            Add(neutral + ".json");
        }

        AddCulture(culture);
        if (!culture.Name.Equals(fallbackCulture, StringComparison.OrdinalIgnoreCase))
            AddCulture(CultureInfo.GetCultureInfo(fallbackCulture));

        return candidates;
    }

    internal static List<JsonFileCandidate> ResolveFileCandidates(CultureInfo culture, string fallbackCulture, IDictionary<string, string> mappings, string key)
    {
        var ns = GetNamespace(key, out _);
        var candidates = new List<JsonFileCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, string cultureName, bool isNamespaceFile = false)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var normalized = path.Replace('\\', '/');
            if (seen.Add(normalized)) candidates.Add(new JsonFileCandidate(normalized, cultureName, isNamespaceFile));
        }

        void AddVariant(string name)
        {
            if (mappings.TryGetValue(name, out var mapped)) Add(mapped, name, ns is not null && IsNamespaceFileName(mapped, ns));
            if (ns is not null)
            {
                Add($"{name}.{ns}.json", name, isNamespaceFile: true);
                Add($"{ns}.{name}.json", name, isNamespaceFile: true);
                if (!name.Equals(ns, StringComparison.OrdinalIgnoreCase))
                    Add(Path.Combine(name, ns + ".json"), name, isNamespaceFile: true);
            }
            Add(name + ".json", name);
            Add(Path.Combine(name, name + ".json"), name);
        }

        void AddCulture(CultureInfo candidateCulture)
        {
            AddVariant(candidateCulture.Name);
            var neutral = candidateCulture.TwoLetterISOLanguageName;
            if (!candidateCulture.Name.Equals(neutral, StringComparison.OrdinalIgnoreCase))
                AddVariant(neutral);
        }

        AddCulture(culture);
        if (!culture.Name.Equals(fallbackCulture, StringComparison.OrdinalIgnoreCase))
            AddCulture(CultureInfo.GetCultureInfo(fallbackCulture));

        return candidates;
    }

    internal static bool IsNamespaceFileName(string candidate, string ns)
    {
        var normalized = candidate.Replace('\\', '/');
        var fileName = ns + ".json";
        if (normalized.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.StartsWith(ns + ".", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase)) return true;
        return normalized.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase) &&
            !("/" + normalized).EndsWith("/" + ns + "/" + fileName, StringComparison.OrdinalIgnoreCase);
    }

    // The first non-empty segment is the namespace when another non-empty segment follows.
    // rest is where the key continues after that first segment.
    internal static string? GetNamespace(string key, out int rest)
    {
        rest = 0;
        string? first = null;
        for (var start = 0; start <= key.Length;)
        {
            var end = key.IndexOf(':', start);
            if (end < 0) end = key.Length;
            var segment = key.AsSpan(start, end - start).Trim();
            if (!segment.IsEmpty)
            {
                if (first is not null) return IsNamespaceName(first) ? first : null;
                first = segment.ToString();
                rest = end + 1;
            }
            start = end + 1;
        }

        return null;
    }

    private static bool IsNamespaceName(string segment) =>
        segment.Length > 0 && segment is not "." and not ".." &&
        segment.IndexOfAny(['/', '\\', '?', '#', '\0', '*', '"', '<', '>', '|']) < 0;

    internal static string GetCacheKey(string resourceName)
    {
        if (resourceName.Contains(Path.DirectorySeparatorChar) || resourceName.Contains(Path.AltDirectorySeparatorChar))
            return Path.GetFileName(resourceName);
        var parts = resourceName.Split('.');
        return parts.Length >= 2 ? $"{parts[^2]}.json" : resourceName;
    }

    // Below the resource folder, it.common.json and it/common.json keep their culture, so the namespace
    // files of different cultures do not share a key. it/it-IT.json is a culture file in a culture folder.
    internal static string? GetNamespaceCacheKey(string resourceName, string folder)
    {
        if (folder.Length == 0 || !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return null;
        int start;
        if (resourceName.StartsWith(folder + ".", StringComparison.OrdinalIgnoreCase))
        {
            start = folder.Length + 1;
        }
        else
        {
            var index = resourceName.IndexOf("." + folder + ".", StringComparison.OrdinalIgnoreCase);
            if (index < 0) return null;
            start = index + folder.Length + 2;
        }
        if (start >= resourceName.Length - 5) return null;

        var stem = resourceName[start..^5];
        var separator = stem.IndexOf('.');
        if (separator < 0 || !TryCulture(stem[..separator], out var culture)) return null;
        var ns = stem[(separator + 1)..];
        if (!IsNamespaceName(ns) || !ns.Contains('.') && ns.Split('-')[0].Equals(culture.Split('-')[0], StringComparison.OrdinalIgnoreCase))
            return null;
        return $"{culture}.{ns}.json";
    }

    internal static bool TryGetValue(JsonElement root, string key, bool isNamespaceFile, out string value) =>
        TryGetValue(root, key, out value) ||
        isNamespaceFile && GetNamespace(key, out var rest) is not null && TryGetValue(root, key, rest, out value);

    internal static bool TryGetValue(JsonElement root, string key, out string value) => TryGetValue(root, key, 0, out value);

    // Walks the colon-separated segments from start, trimmed and skipping empty ones, without allocating them.
    internal static bool TryGetValue(JsonElement root, string key, int start, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(key)) return false;

        var current = root;
        while (start <= key.Length)
        {
            var end = key.IndexOf(':', start);
            if (end < 0) end = key.Length;
            var segment = key.AsSpan(start, end - start).Trim();
            if (!segment.IsEmpty && (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)))
                return false;
            start = end + 1;
        }

        if (current.ValueKind != JsonValueKind.String) return false;
        value = current.GetString() ?? string.Empty;
        return true;
    }
}
