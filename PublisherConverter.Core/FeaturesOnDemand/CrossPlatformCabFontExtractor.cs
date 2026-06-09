using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core.FeaturesOnDemand
{
    /// <summary>
    /// Production <see cref="ICabFontExtractor"/> that picks the right backend at
    /// runtime: native <c>expand.exe</c> on Windows (full CAB-method coverage),
    /// the managed reader everywhere else.
    /// </summary>
    public sealed class CrossPlatformCabFontExtractor : ICabFontExtractor
    {
        private readonly ICabFontExtractor _inner;

        public CrossPlatformCabFontExtractor()
        {
            _inner = OperatingSystem.IsWindows()
                ? new WindowsExpandCabFontExtractor()
                : new ManagedCabFontExtractor();
        }

        public IReadOnlyList<string> Enumerate(string cabPath) => _inner.Enumerate(cabPath);

        public Task<IReadOnlyList<ExtractedFont>> ExtractFontsAsync(string cabPath, string destinationDir, CancellationToken cancellationToken)
            => _inner.ExtractFontsAsync(cabPath, destinationDir, cancellationToken);
    }
}
