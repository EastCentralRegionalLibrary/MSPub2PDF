using System;
using System.Collections.Generic;
using System.Linq;

namespace PublisherConverter.Core
{
    /// <summary>
    /// Aggregates the orchestrator's per-document font-preflight findings into
    /// a missing-font → affected-files map. This is the surface a future
    /// automatic font-installer will consume: it knows what fonts to fetch and,
    /// once a font is installed, which source files become re-runnable.
    /// The orchestrator also calls <see cref="EnumerateSummaryLines"/> to emit
    /// a human-readable wrap-up at the end of the log.
    /// </summary>
    public sealed class FontPreflightReport
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, List<string>> _filesByFont =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public void RecordMissingFonts(string sourceFilePath, IEnumerable<string> missingFonts)
        {
            if (string.IsNullOrEmpty(sourceFilePath)) throw new ArgumentException("Source file path is required.", nameof(sourceFilePath));
            if (missingFonts == null) return;

            lock (_lock)
            {
                foreach (var font in missingFonts)
                {
                    if (string.IsNullOrWhiteSpace(font)) continue;

                    if (!_filesByFont.TryGetValue(font, out var list))
                    {
                        list = new List<string>();
                        _filesByFont[font] = list;
                    }
                    if (!list.Contains(sourceFilePath, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(sourceFilePath);
                    }
                }
            }
        }

        /// <summary>
        /// Snapshot of the report keyed by missing font family. The future
        /// auto-installer iterates this: each key is a fetch target, each
        /// value is the set of files to re-queue once the font lands.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> FilesByMissingFont
        {
            get
            {
                lock (_lock)
                {
                    var snapshot = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in _filesByFont)
                    {
                        snapshot[kv.Key] = kv.Value.ToList();
                    }
                    return snapshot;
                }
            }
        }

        public bool HasFindings
        {
            get { lock (_lock) return _filesByFont.Count > 0; }
        }

        public int DistinctMissingFontCount
        {
            get { lock (_lock) return _filesByFont.Count; }
        }

        /// <summary>
        /// Human-readable summary, one line per emission. The orchestrator
        /// pumps these through the progress reporter at the end of a run.
        /// </summary>
        public IEnumerable<string> EnumerateSummaryLines()
        {
            Dictionary<string, List<string>> snapshot;
            lock (_lock)
            {
                snapshot = new Dictionary<string, List<string>>(_filesByFont, StringComparer.OrdinalIgnoreCase);
            }

            if (snapshot.Count == 0)
            {
                yield return "Font pre-flight: no missing fonts detected.";
                yield break;
            }

            yield return $"Font pre-flight summary — {snapshot.Count} missing font(s) across the batch:";
            foreach (var kv in snapshot.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                yield return $"  • {kv.Key} — affects {kv.Value.Count} file(s):";
                foreach (var path in kv.Value)
                {
                    yield return $"      - {path}";
                }
            }
        }
    }
}
