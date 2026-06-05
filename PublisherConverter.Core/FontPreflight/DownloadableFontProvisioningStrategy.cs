using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    /// <summary>
    /// Generic "direct URL" fallback driven by the <c>externalFallbacks</c>
    /// section of FontMapping.json. Use this for one-off custom URLs that
    /// don't fit the Google Fonts or GitHub release patterns. The
    /// download/register mechanics are delegated to
    /// <see cref="IUserFontInstaller"/> so the install location and HKCU
    /// bookkeeping stay consistent across every strategy.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class DownloadableFontProvisioningStrategy : IFontProvisioningStrategy
    {
        private readonly FontMappingTable _mappings;
        private readonly IUserFontInstaller _installer;

        public DownloadableFontProvisioningStrategy(FontMappingTable mappings, IUserFontInstaller installer)
        {
            _mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));
            _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        }

        public string Name => "DirectUrlFallback";

        public bool CanResolve(string fontFamily) => _mappings.TryGetFallback(fontFamily, out _);

        public Task<bool> TryResolveAsync(string fontFamily, IList<string> log, CancellationToken cancellationToken)
        {
            if (!_mappings.TryGetFallback(fontFamily, out var fallback)) return Task.FromResult(false);

            log.Add($"    fallback={fallback.Name} url={fallback.Url}");

            string installFamily = !string.IsNullOrWhiteSpace(fallback.Name) ? fallback.Name : fontFamily;
            return _installer.InstallFromUrlAsync(installFamily, fallback.Url, fallback.FileName, log, cancellationToken);
        }
    }
}
