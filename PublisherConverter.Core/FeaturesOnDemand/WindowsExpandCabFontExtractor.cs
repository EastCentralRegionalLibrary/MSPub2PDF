using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core.FeaturesOnDemand
{
    /// <summary>
    /// Windows <see cref="ICabFontExtractor"/> that decompresses with the native
    /// <c>expand.exe</c> (as the Python reference does), so every CAB compression
    /// method — including LZX and Quantum — is handled.
    ///
    /// Enumeration still uses the managed <see cref="CabArchiveReader"/>: file
    /// metadata is never compressed, so listing is reliable and lets us drive
    /// targeted <c>expand -F:&lt;name&gt;</c> calls that materialise <em>only</em>
    /// the font files instead of the whole cabinet.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsExpandCabFontExtractor : ICabFontExtractor
    {
        private readonly IProcessRunner _runner;
        private readonly TimeSpan _timeout;

        public WindowsExpandCabFontExtractor(IProcessRunner? runner = null, TimeSpan? timeout = null)
        {
            _runner = runner ?? new DefaultProcessRunner();
            _timeout = timeout ?? TimeSpan.FromMinutes(2);
        }

        public IReadOnlyList<string> Enumerate(string cabPath)
        {
            var reader = CabArchiveReader.FromFile(cabPath);
            var names = new List<string>(reader.Entries.Count);
            foreach (var e in reader.Entries) names.Add(e.Name);
            return names;
        }

        public async Task<IReadOnlyList<ExtractedFont>> ExtractFontsAsync(string cabPath, string destinationDir, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(destinationDir);
            var reader = CabArchiveReader.FromFile(cabPath);
            var extracted = new List<ExtractedFont>();

            foreach (var entry in reader.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string leaf = LeafName(entry.Name);
                if (!FontFileInspector.IsFontFile(leaf)) continue;

                // expand.exe -F:<name> <cab> <destDir> materialises just this file.
                string args = $"\"-F:{entry.Name}\" \"{cabPath}\" \"{destinationDir}\"";
                var result = await _runner.RunAsync("expand.exe", args, _timeout, cancellationToken).ConfigureAwait(false);

                // expand preserves the entry's internal sub-path, so a path-qualified
                // name (e.g. "Fonts\\mingliu.ttc") lands in a sub-folder, not at the
                // flat leaf. Accept whichever the tool actually wrote.
                string nested = Path.Combine(destinationDir, entry.Name.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
                string flat = Path.Combine(destinationDir, leaf);
                string targetPath = File.Exists(nested) ? nested : flat;
                if (result.ExitCode != 0 || !File.Exists(targetPath))
                {
                    // Per-file isolation — skip and continue with the next entry.
                    continue;
                }

                IReadOnlyList<string> families;
                try { families = FontFileInspector.GetFamilyNames(targetPath); }
                catch { families = Array.Empty<string>(); }

                extracted.Add(new ExtractedFont
                {
                    FilePath = targetPath,
                    FileName = leaf,
                    FamilyNames = families,
                    IsCollection = FontFileInspector.IsCollection(leaf),
                });
            }

            return extracted;
        }

        private static string LeafName(string name)
        {
            int slash = name.LastIndexOfAny(new[] { '\\', '/' });
            return slash >= 0 ? name.Substring(slash + 1) : name;
        }
    }
}
