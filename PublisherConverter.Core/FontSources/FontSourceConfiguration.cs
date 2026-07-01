using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PublisherConverter.Core.FontSources
{
    /// <summary>Kind of source, which selects the resolver that handles it.</summary>
    public enum FontSourceType
    {
        Local = 0,
        GoogleFonts,
        VendorRepo,
        Community,
    }

    /// <summary>What a community source tries first.</summary>
    public enum ProbeStrategy
    {
        SlugThenSearch = 0,
        SearchOnly,
        SlugOnly,
    }

    /// <summary>How an unknown/ambiguous license is treated.</summary>
    public enum UnknownLicenseAction
    {
        ManualReviewRequired = 0,
        Reject,
        Allow,
    }

    /// <summary>Repository coordinates for a repo-backed source.</summary>
    public sealed class RepoSpec
    {
        public string Owner { get; set; } = string.Empty;
        public string Repo { get; set; } = string.Empty;
        public string Branch { get; set; } = "main";
        public string? Tag { get; set; }
        public string? ReleaseAssetPattern { get; set; }
    }

    /// <summary>Archive (zip) handling hints for a source.</summary>
    public sealed class ArchiveHints
    {
        /// <summary>Only these extensions are extracted from an archive.</summary>
        public List<string> ExtractExtensions { get; set; } = new List<string> { ".ttf" };

        /// <summary>Preferred in-archive path fragments for .ttf files (e.g. "fonts/ttf/").</summary>
        public List<string> TtfPathHints { get; set; } = new List<string>();

        /// <summary>File names that carry license text inside the archive.</summary>
        public List<string> LicenseFileNames { get; set; } = new List<string> { "OFL.txt", "LICENSE", "LICENSE.txt", "license.txt", "readme.txt" };
    }

    /// <summary>License-policy knobs, all configuration-driven.</summary>
    public sealed class LicensePolicyOptions
    {
        /// <summary>Master switch for auto-installing non-Microsoft fonts.</summary>
        public bool AutoInstallNonMicrosoft { get; set; } = true;

        /// <summary>Keywords whose presence marks a license clearly installable.</summary>
        public List<string> AllowedKeywords { get; set; } = new List<string>
        {
            "SIL Open Font License", "OFL", "Apache License", "Apache-2.0",
            "Ubuntu Font License", "UFL", "MIT License",
        };

        /// <summary>Keywords that force manual review (e.g. personal-use-only).</summary>
        public List<string> ManualReviewKeywords { get; set; } = new List<string>
        {
            "free for personal use", "personal use only", "donationware", "demo", "shareware",
        };

        /// <summary>Keywords that reject a payload outright.</summary>
        public List<string> RejectKeywords { get; set; } = new List<string>
        {
            "no redistribution", "commercial use prohibited", "all rights reserved",
        };

        public UnknownLicenseAction UnknownLicenseAction { get; set; } = UnknownLicenseAction.ManualReviewRequired;
    }

    /// <summary>
    /// Cross-cutting policy: which layers run, whether community sources are on,
    /// the vendor allowlist, timeout budgets, and license policy.
    /// </summary>
    public sealed class SourcePolicyOptions
    {
        public List<ResolutionLayer> EnabledLayers { get; set; } = new List<ResolutionLayer>
        {
            ResolutionLayer.LocalCache, ResolutionLayer.Scratch, ResolutionLayer.FeaturesOnDemand,
            ResolutionLayer.GoogleFonts, ResolutionLayer.VendorRepo, ResolutionLayer.Community,
        };

        public bool CommunityEnabled { get; set; } = true;

        /// <summary>Source ids permitted to act as vendor routes. Empty = all enabled vendor sources.</summary>
        public List<string> AllowlistedVendors { get; set; } = new List<string>();

        public int ProbeTimeoutMs { get; set; } = 3000;
        public int DownloadTimeoutMs { get; set; } = 60000;

        /// <summary>
        /// Optional GitHub API token used as a bearer Authorization header on
        /// api.github.com calls (release-archive sources). Raises the
        /// unauthenticated 60 req/hour/IP rate limit to 5,000/hour. Never
        /// required — when absent the resolver also checks the GITHUB_TOKEN
        /// environment variable, and proceeds unauthenticated if neither is set.
        /// Never ship a default value here.
        /// </summary>
        public string? GitHubToken { get; set; }

        public LicensePolicyOptions License { get; set; } = new LicensePolicyOptions();
    }

    /// <summary>One configured source definition (the unit onboarded via config).</summary>
    public sealed class FontSourceDefinition
    {
        public string Id { get; set; } = string.Empty;
        public FontSourceType Type { get; set; }
        public bool Enabled { get; set; } = true;
        public int Priority { get; set; } = 100;

        public string? BaseUrl { get; set; }
        public string RawBaseUrl { get; set; } = "https://raw.githubusercontent.com";
        public string ApiBaseUrl { get; set; } = "https://api.github.com";

        public RepoSpec? Repo { get; set; }

        /// <summary>Path templates with placeholders {licenseDir} {slug} {FamilyNoSpace} {Family} {Style} {style}.</summary>
        public List<string> PathTemplates { get; set; } = new List<string>();

        /// <summary>License sub-directories to probe (Google Fonts: ofl/apache/ufl).</summary>
        public List<string> LicenseDirs { get; set; } = new List<string>();

        /// <summary>
        /// Path template of the per-family metadata file that authoritatively
        /// lists the family's font files (google/fonts METADATA.pb). Used when no
        /// static file matches the path templates, e.g. variable-only families.
        /// </summary>
        public string MetadataPathTemplate { get; set; } = "{licenseDir}/{slug}/METADATA.pb";

        /// <summary>Vendor routing patterns (regex) matched against the requested family.</summary>
        public List<string> RoutingPatterns { get; set; } = new List<string>();

        /// <summary>Styles to attempt (Regular, Bold, Italic, BoldItalic).</summary>
        public List<string> Styles { get; set; } = new List<string> { "Regular" };

        public List<string> SupportedExtensions { get; set; } = new List<string> { ".ttf" };

        public ArchiveHints? Archive { get; set; }

        public string? LicenseHint { get; set; }
        public ProbeStrategy ProbeStrategy { get; set; } = ProbeStrategy.SlugThenSearch;
        public int? TimeoutOverrideMs { get; set; }

        // Community templates with {slug} / {query} placeholders.
        public string? SlugTemplate { get; set; }
        public string? SearchTemplate { get; set; }

        /// <summary>
        /// HTTP request headers sent with every download/probe for this source.
        /// Use for sources that require a Referer, Accept, or UA to serve archives.
        /// </summary>
        public Dictionary<string, string> DefaultRequestHeaders { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>Raw on-disk shape of FontSources.json.</summary>
    public sealed class FontSourcesFile
    {
        [JsonPropertyName("policy")]
        public SourcePolicyOptions Policy { get; set; } = new SourcePolicyOptions();

        /// <summary>Family aliases: requested family → canonical family.</summary>
        [JsonPropertyName("aliases")]
        public Dictionary<string, string> Aliases { get; set; } = new Dictionary<string, string>();

        /// <summary>Style tokens stripped from a requested name before resolution.</summary>
        [JsonPropertyName("styleSuffixes")]
        public List<string> StyleSuffixes { get; set; } = new List<string>();

        [JsonPropertyName("sources")]
        public List<FontSourceDefinition> Sources { get; set; } = new List<FontSourceDefinition>();
    }
}
