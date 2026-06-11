using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core.FeaturesOnDemand
{
    /// <summary>
    /// Cross-platform <see cref="ICabFontExtractor"/> built on the managed
    /// <see cref="CabArchiveReader"/>. Used on non-Windows platforms (and any
    /// time a native tool is unavailable). Only font entries are materialised to
    /// disk; the rest of the cabinet is never decompressed.
    /// </summary>
    public sealed class ManagedCabFontExtractor : ICabFontExtractor
    {
        public IReadOnlyList<string> Enumerate(string cabPath)
        {
            var reader = CabArchiveReader.FromFile(cabPath);
            var names = new List<string>(reader.Entries.Count);
            foreach (var e in reader.Entries) names.Add(e.Name);
            return names;
        }

        public Task<IReadOnlyList<ExtractedFont>> ExtractFontsAsync(string cabPath, string destinationDir, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(destinationDir);
            var reader = CabArchiveReader.FromFile(cabPath);
            var extracted = new List<ExtractedFont>();

            foreach (var entry in reader.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string leaf = LeafName(entry.Name);
                if (!FontFileInspector.IsFontFile(leaf)) continue;

                try
                {
                    byte[] bytes = reader.ReadEntry(entry);
                    string targetPath = Path.Combine(destinationDir, leaf);
                    File.WriteAllBytes(targetPath, bytes);

                    var families = FontFileInspector.GetFamilyNames(bytes);
                    extracted.Add(new ExtractedFont
                    {
                        FilePath = targetPath,
                        FileName = leaf,
                        FamilyNames = families,
                        IsCollection = FontFileInspector.IsCollection(leaf),
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Per-file isolation: skip an unreadable/unsupported entry.
                }
            }

            return Task.FromResult<IReadOnlyList<ExtractedFont>>(extracted);
        }

        private static string LeafName(string name)
        {
            // CAB names can contain backslash path separators.
            int slash = name.LastIndexOfAny(new[] { '\\', '/' });
            return slash >= 0 ? name.Substring(slash + 1) : name;
        }
    }
}
