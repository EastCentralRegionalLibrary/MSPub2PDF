using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PublisherConverter.Core.FontSources
{
    /// <summary>Thrown when FontSources.json is missing required/valid data.</summary>
    public sealed class FontSourceConfigurationException : Exception
    {
        public FontSourceConfigurationException(string message) : base(message) { }
        public FontSourceConfigurationException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>A vendor source plus its compiled routing matcher.</summary>
    public sealed class VendorRoute
    {
        public required FontSourceDefinition Source { get; init; }
        public required IReadOnlyList<Regex> Patterns { get; init; }

        public bool Matches(string text)
        {
            foreach (var rx in Patterns)
            {
                if (rx.IsMatch(text)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Validated, normalized, queryable view of FontSources.json. Loading fails
    /// fast and clearly on malformed required data; everything the resolvers need
    /// (sorted sources, compiled routing regexes, the alias map, the style-suffix
    /// set, and policy) is computed once here so resolver code stays data-driven.
    /// </summary>
    public sealed class FontSourceConfiguration
    {
        private readonly Dictionary<string, string> _aliases; // normalized requested -> canonical
        private readonly HashSet<string> _styleSuffixes;      // lower-case

        public SourcePolicyOptions Policy { get; }
        public IReadOnlyList<FontSourceDefinition> Sources { get; }
        public IReadOnlyList<VendorRoute> VendorRoutes { get; }

        private FontSourceConfiguration(
            SourcePolicyOptions policy,
            IReadOnlyList<FontSourceDefinition> sources,
            IReadOnlyList<VendorRoute> vendorRoutes,
            Dictionary<string, string> aliases,
            HashSet<string> styleSuffixes)
        {
            Policy = policy;
            Sources = sources;
            VendorRoutes = vendorRoutes;
            _aliases = aliases;
            _styleSuffixes = styleSuffixes;
        }

        public bool IsLayerEnabled(ResolutionLayer layer) => Policy.EnabledLayers.Contains(layer);

        public IReadOnlyCollection<string> StyleSuffixes => _styleSuffixes;

        /// <summary>Resolves an alias (by normalized key) to its canonical family, or returns the input.</summary>
        public string ResolveAlias(string family)
        {
            if (string.IsNullOrWhiteSpace(family)) return family;
            return _aliases.TryGetValue(FontNameNormalizer.Normalize(family), out var canonical) ? canonical : family;
        }

        public FontSourceDefinition? FirstEnabled(FontSourceType type)
            => Sources.FirstOrDefault(s => s.Enabled && s.Type == type);

        public IEnumerable<FontSourceDefinition> Enabled(FontSourceType type)
            => Sources.Where(s => s.Enabled && s.Type == type);

        /// <summary>Vendor route for a family, honoring the policy allowlist; null when none match.</summary>
        public VendorRoute? RouteVendor(string family)
        {
            foreach (var route in VendorRoutes)
            {
                if (!route.Matches(family)) continue;
                if (Policy.AllowlistedVendors.Count > 0 && !Policy.AllowlistedVendors.Contains(route.Source.Id)) continue;
                return route;
            }
            return null;
        }

        // ---- loading ----

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
        };

        public static FontSourceConfiguration LoadFromFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FontSourceConfigurationException($"FontSources.json not found at '{path}'.");
            try
            {
                return LoadFromJson(File.ReadAllText(path));
            }
            catch (FontSourceConfigurationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new FontSourceConfigurationException($"Failed to read FontSources.json: {ex.Message}", ex);
            }
        }

        public static FontSourceConfiguration LoadFromJson(string json)
        {
            FontSourcesFile? file;
            try
            {
                file = JsonSerializer.Deserialize<FontSourcesFile>(json, JsonOpts);
            }
            catch (JsonException ex)
            {
                throw new FontSourceConfigurationException($"FontSources.json is not valid JSON: {ex.Message}", ex);
            }
            if (file == null) throw new FontSourceConfigurationException("FontSources.json deserialized to null.");

            return Validate(file);
        }

        private static FontSourceConfiguration Validate(FontSourcesFile file)
        {
            var policy = file.Policy ?? new SourcePolicyOptions();
            if (policy.ProbeTimeoutMs <= 0) throw new FontSourceConfigurationException("policy.probeTimeoutMs must be > 0.");
            if (policy.DownloadTimeoutMs <= 0) throw new FontSourceConfigurationException("policy.downloadTimeoutMs must be > 0.");

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var vendorRoutes = new List<VendorRoute>();

            foreach (var src in file.Sources)
            {
                if (string.IsNullOrWhiteSpace(src.Id))
                    throw new FontSourceConfigurationException("Every source needs a non-empty 'id'.");
                if (!seenIds.Add(src.Id))
                    throw new FontSourceConfigurationException($"Duplicate source id '{src.Id}'.");

                NormalizeExtensions(src);

                switch (src.Type)
                {
                    case FontSourceType.GoogleFonts:
                        RequireRepo(src);
                        if (src.LicenseDirs.Count == 0)
                            throw new FontSourceConfigurationException($"GoogleFonts source '{src.Id}' needs 'licenseDirs'.");
                        if (src.PathTemplates.Count == 0)
                            throw new FontSourceConfigurationException($"GoogleFonts source '{src.Id}' needs 'pathTemplates'.");
                        break;

                    case FontSourceType.VendorRepo:
                        if (src.RoutingPatterns.Count == 0)
                            throw new FontSourceConfigurationException($"VendorRepo source '{src.Id}' needs 'routingPatterns'.");
                        RequireRepo(src);
                        bool hasPaths = src.PathTemplates.Count > 0;
                        bool hasRelease = !string.IsNullOrWhiteSpace(src.Repo!.ReleaseAssetPattern);
                        if (!hasPaths && !hasRelease)
                            throw new FontSourceConfigurationException($"VendorRepo source '{src.Id}' needs 'pathTemplates' or a release asset pattern.");

                        var compiled = CompilePatterns(src);
                        if (src.Enabled) vendorRoutes.Add(new VendorRoute { Source = src, Patterns = compiled });
                        break;

                    case FontSourceType.Community:
                        if (string.IsNullOrWhiteSpace(src.SlugTemplate) && string.IsNullOrWhiteSpace(src.SearchTemplate) && string.IsNullOrWhiteSpace(src.BaseUrl))
                            throw new FontSourceConfigurationException($"Community source '{src.Id}' needs a baseUrl or slug/search template.");
                        break;

                    case FontSourceType.Local:
                        break;
                }
            }

            // Sort by ascending priority (lower = earlier within its layer).
            var sources = file.Sources.OrderBy(s => s.Priority).ToList();
            vendorRoutes = vendorRoutes.OrderBy(r => r.Source.Priority).ToList();

            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in file.Aliases)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
                aliases[FontNameNormalizer.Normalize(kv.Key)] = kv.Value;
            }

            var styleSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in file.StyleSuffixes)
            {
                if (!string.IsNullOrWhiteSpace(s)) styleSuffixes.Add(s.Trim());
            }

            return new FontSourceConfiguration(policy, sources, vendorRoutes, aliases, styleSuffixes);
        }

        private static void RequireRepo(FontSourceDefinition src)
        {
            if (src.Repo == null || string.IsNullOrWhiteSpace(src.Repo.Owner) || string.IsNullOrWhiteSpace(src.Repo.Repo))
                throw new FontSourceConfigurationException($"Source '{src.Id}' needs repo.owner and repo.repo.");
        }

        private static IReadOnlyList<Regex> CompilePatterns(FontSourceDefinition src)
        {
            var list = new List<Regex>();
            foreach (var p in src.RoutingPatterns)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                try
                {
                    list.Add(new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant));
                }
                catch (ArgumentException ex)
                {
                    throw new FontSourceConfigurationException($"Source '{src.Id}' has an invalid routing pattern '{p}': {ex.Message}", ex);
                }
            }
            if (list.Count == 0)
                throw new FontSourceConfigurationException($"VendorRepo source '{src.Id}' has no usable routing patterns.");
            return list;
        }

        private static void NormalizeExtensions(FontSourceDefinition src)
        {
            for (int i = 0; i < src.SupportedExtensions.Count; i++)
            {
                string e = src.SupportedExtensions[i].Trim().ToLowerInvariant();
                if (!e.StartsWith('.')) e = "." + e;
                src.SupportedExtensions[i] = e;
            }
        }
    }
}
