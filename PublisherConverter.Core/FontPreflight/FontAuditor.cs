using System;
using System.Collections.Generic;

namespace PublisherConverter.Core
{
    public sealed class FontAuditor : IFontAuditor
    {
        private readonly IPublisherFontExtractor _extractor;
        private readonly FontAvailabilityCache _cache;

        public FontAuditor(IPublisherFontExtractor extractor, FontAvailabilityCache cache)
        {
            _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        }

        public IReadOnlyList<string> ResolveMissingFonts(string pubPath)
        {
            var referenced = _extractor.ExtractFontNames(pubPath);
            var missing = new List<string>();
            foreach (var f in referenced)
            {
                if (!_cache.IsInstalled(f)) missing.Add(f);
            }
            missing.Sort(StringComparer.OrdinalIgnoreCase);
            return missing;
        }

        public void AuditFonts(string pubPath, RenderResult result)
        {
            var missing = ResolveMissingFonts(pubPath);
            result.MissingFontsCount = missing.Count;
            result.MissingFontsList = missing.Count == 0 ? "None" : string.Join(" | ", missing);
        }
    }

    /// <summary>
    /// Inert auditor used when the orchestrator is wired without font pre-flight
    /// (most unit tests). Always reports "no missing fonts" so the engine's
    /// behaviour matches the pre-feature baseline.
    /// </summary>
    public sealed class NoOpFontAuditor : IFontAuditor
    {
        public IReadOnlyList<string> ResolveMissingFonts(string pubPath) => Array.Empty<string>();

        public void AuditFonts(string pubPath, RenderResult result)
        {
            result.MissingFontsCount = 0;
            result.MissingFontsList = "None";
        }
    }
}
