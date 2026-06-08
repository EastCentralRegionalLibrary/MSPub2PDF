using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    public sealed class FontResolutionOutcome
    {
        public IReadOnlyList<string> InitiallyMissing { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Resolved { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> StillMissing { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();
    }

    public interface IFontResolver
    {
        /// <summary>
        /// Best-effort attempt to make each of the supplied missing fonts
        /// available on the local system. Returns the resolved / still-missing
        /// split along with a human-readable per-attempt log the caller can
        /// pump into its progress reporter.
        /// </summary>
        Task<FontResolutionOutcome> ResolveMissingFontsAsync(
            IReadOnlyList<string> missingFonts,
            CancellationToken cancellationToken);
    }

    public sealed class NoOpFontResolver : IFontResolver
    {
        public Task<FontResolutionOutcome> ResolveMissingFontsAsync(IReadOnlyList<string> missingFonts, CancellationToken cancellationToken)
            => Task.FromResult(new FontResolutionOutcome
            {
                InitiallyMissing = missingFonts ?? Array.Empty<string>(),
                Resolved = Array.Empty<string>(),
                StillMissing = missingFonts ?? Array.Empty<string>(),
                Log = Array.Empty<string>(),
            });
    }

    /// <summary>
    /// Walks a list of missing fonts and gives each registered
    /// <see cref="IFontProvisioningStrategy"/> a chance to resolve it.
    ///
    /// Behaviour:
    ///   * If a font is already available (cache hit), it's marked resolved
    ///     without invoking any strategy — idempotent across repeated runs.
    ///   * Strategies are tried in the order supplied. The first to return
    ///     true wins; subsequent strategies for that font are skipped.
    ///   * On a successful provision the font is marked installed in the
    ///     availability cache (so the original family name resolves even
    ///     when a downloadable substitute was installed under a different
    ///     family) and the provider is asked to refresh so any newly-
    ///     registered native names also become visible.
    ///   * Strategy exceptions are caught and logged as failures — the
    ///     resolver never throws.
    /// </summary>
    public sealed class FontResolver : IFontResolver
    {
        private readonly FontAvailabilityCache _cache;
        private readonly IReadOnlyList<IFontProvisioningStrategy> _strategies;

        public FontResolver(FontAvailabilityCache cache, IEnumerable<IFontProvisioningStrategy> strategies)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _strategies = strategies != null
                ? new List<IFontProvisioningStrategy>(strategies)
                : (IReadOnlyList<IFontProvisioningStrategy>)Array.Empty<IFontProvisioningStrategy>();
        }

        public async Task<FontResolutionOutcome> ResolveMissingFontsAsync(IReadOnlyList<string> missingFonts, CancellationToken cancellationToken)
        {
            var input = missingFonts ?? (IReadOnlyList<string>)Array.Empty<string>();
            var log = new List<string>();
            var resolved = new List<string>();
            var stillMissing = new List<string>();
            bool refreshNeeded = false;

            log.Add($"Font auto-resolver: attempting to provision {input.Count} missing font(s) using {_strategies.Count} strategy(ies).");

            foreach (var font in input)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_cache.IsInstalled(font))
                {
                    log.Add($"  • {font} — already available; skipping.");
                    resolved.Add(font);
                    continue;
                }

                bool fontResolved = false;
                bool anyStrategyApplied = false;
                foreach (var strategy in _strategies)
                {
                    if (!strategy.CanResolve(font)) continue;
                    anyStrategyApplied = true;

                    log.Add($"  • {font} — trying {strategy.Name}…");
                    bool ok;
                    try
                    {
                        ok = await strategy.TryResolveAsync(font, log, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        log.Add($"    ! {strategy.Name} threw: {ex.Message}");
                        ok = false;
                    }

                    if (ok)
                    {
                        log.Add($"    ✓ {strategy.Name} resolved {font}.");
                        _cache.MarkInstalled(font);
                        refreshNeeded = true;
                        fontResolved = true;
                        break;
                    }
                    log.Add($"    × {strategy.Name} did not resolve {font}; trying next strategy if available.");
                }

                if (!anyStrategyApplied)
                {
                    log.Add($"  • {font} — no strategy in the mapping table; nothing to try.");
                }

                if (fontResolved) resolved.Add(font);
                else stillMissing.Add(font);
            }

            if (refreshNeeded)
            {
                log.Add("  Refreshing installed-font index after successful provisioning…");
                try { _cache.RefreshProvider(); } catch (Exception ex) { log.Add($"    ! cache refresh failed: {ex.Message}"); }

                // A refresh might surface families we'd previously marked as
                // still-missing — re-check before reporting.
                if (stillMissing.Count > 0)
                {
                    var rechecked = new List<string>();
                    foreach (var font in stillMissing)
                    {
                        if (_cache.IsInstalled(font))
                        {
                            log.Add($"  • {font} — became available after refresh.");
                            resolved.Add(font);
                        }
                        else
                        {
                            rechecked.Add(font);
                        }
                    }
                    stillMissing = rechecked;
                }
            }

            log.Add($"Font auto-resolver: resolved {resolved.Count}, still missing {stillMissing.Count}.");

            return new FontResolutionOutcome
            {
                InitiallyMissing = input,
                Resolved = resolved,
                StillMissing = stillMissing,
                Log = log,
            };
        }
    }
}
