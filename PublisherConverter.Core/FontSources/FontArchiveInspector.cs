using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core.FontSources
{
    /// <summary>A .ttf pulled out of an archive, kept in memory for validation.</summary>
    public sealed class ArchivedFont
    {
        public required string EntryName { get; init; }
        public required byte[] Data { get; init; }
    }

    /// <summary>Result of inspecting an archive: only the .ttf payloads plus any license text.</summary>
    public sealed class ArchiveInspection
    {
        public IReadOnlyList<ArchivedFont> Fonts { get; init; } = Array.Empty<ArchivedFont>();
        public string? LicenseText { get; init; }
        public IReadOnlyList<string> LicenseFileNames { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Enumerates a ZIP archive and extracts only the font files whose extension
    /// is allowed (just <c>.ttf</c> by default), ignoring images, docs, and other
    /// payloads. License/readme files named in the hints are read out for the
    /// license evaluator. Operates on a stream; nothing is written to disk here.
    /// </summary>
    public sealed class FontArchiveInspector
    {
        public async Task<ArchiveInspection> InspectAsync(Stream archiveStream, ArchiveHints? hints, CancellationToken cancellationToken)
        {
            var extract = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in (hints?.ExtractExtensions ?? new List<string> { ".ttf" }))
            {
                string ext = e.StartsWith('.') ? e : "." + e;
                extract.Add(ext);
            }
            var licenseNames = new HashSet<string>(
                hints?.LicenseFileNames ?? new List<string> { "OFL.txt", "LICENSE", "LICENSE.txt", "license.txt", "readme.txt" },
                StringComparer.OrdinalIgnoreCase);

            var fonts = new List<ArchivedFont>();
            var licenseBuilder = new StringBuilder();
            var foundLicenseFiles = new List<string>();

            // Validate ZIP magic before handing to ZipArchive, to produce a clear error
            // rather than a misleading "Central Directory corrupt" exception when the
            // server returned an HTML error/challenge page instead of the archive.
            if (archiveStream.CanSeek && archiveStream.Length >= 4)
            {
                archiveStream.Position = 0;
                var magic = new byte[4];
                int read = archiveStream.Read(magic, 0, 4);
                if (read == 4
                    && !(magic[0] == 0x50 && magic[1] == 0x4B && magic[2] == 0x03 && magic[3] == 0x04))
                {
                    throw new InvalidDataException(
                        $"Downloaded payload is not a ZIP file (first bytes: {magic[0]:X2} {magic[1]:X2} {magic[2]:X2} {magic[3]:X2}). " +
                        "The server may require specific HTTP headers or a session cookie.");
                }
                archiveStream.Position = 0;
            }

            using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name)) continue; // directory

                string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (extract.Contains(ext))
                {
                    using var ms = new MemoryStream();
                    using (var es = entry.Open())
                    {
                        await es.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                    }
                    fonts.Add(new ArchivedFont { EntryName = entry.FullName, Data = ms.ToArray() });
                }
                else if (licenseNames.Contains(entry.Name))
                {
                    foundLicenseFiles.Add(entry.FullName);
                    using var es = entry.Open();
                    using var reader = new StreamReader(es, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                    if (licenseBuilder.Length > 0) licenseBuilder.Append('\n');
                    licenseBuilder.Append(text);
                }
            }

            return new ArchiveInspection
            {
                Fonts = fonts,
                LicenseText = licenseBuilder.Length > 0 ? licenseBuilder.ToString() : null,
                LicenseFileNames = foundLicenseFiles,
            };
        }
    }
}
