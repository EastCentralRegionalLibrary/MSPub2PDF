using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace PublisherConverter.Core
{
    public class ArchiveService : IArchiveService
    {
        private readonly string _targetBackupRoot;
        private readonly bool _compress;
        private string? _stagingDirectory;

        public ArchiveService(string targetBackupRoot, bool compress = false)
        {
            _targetBackupRoot = targetBackupRoot;
            _compress = compress;
        }

        public bool Compress => _compress;

        /// <summary>
        /// Creates a unique, timestamped backup directory.
        /// </summary>
        public void Initialize()
        {
            if (string.IsNullOrEmpty(_targetBackupRoot)) return;

            if (!Directory.Exists(_targetBackupRoot))
            {
                Directory.CreateDirectory(_targetBackupRoot);
            }

            _stagingDirectory = Path.Combine(_targetBackupRoot, $"PublisherBackup_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(_stagingDirectory);
        }

        /// <summary>
        /// Copies a file to the staging directory, preserving relative path structure.
        /// </summary>
        public void StageFile(string sourceFullPath, string relativePath, string fileName)
        {
            if (string.IsNullOrEmpty(_stagingDirectory) || !Directory.Exists(_stagingDirectory)) return;

            string targetedSubFolder = Path.Combine(_stagingDirectory, relativePath.TrimStart('\\', '/'));

            if (!Directory.Exists(targetedSubFolder))
            {
                Directory.CreateDirectory(targetedSubFolder);
            }

            File.Copy(sourceFullPath, Path.Combine(targetedSubFolder, fileName), true);
        }

        /// <summary>
        /// Finalizes the archive by optionally compressing it and cleaning up the staging directory.
        /// </summary>
        public void FinalizeArchive()
        {
            if (string.IsNullOrEmpty(_stagingDirectory) || !Directory.Exists(_stagingDirectory)) return;

            try
            {
                // Verify if any items were successfully staged before creating the ZIP
                bool hasFiles = Directory.GetFiles(_stagingDirectory, "*", SearchOption.AllDirectories).Length > 0;

                if (_compress && hasFiles)
                {
                    string finalZipPath = _stagingDirectory + ".zip";
                    ZipFile.CreateFromDirectory(_stagingDirectory, finalZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
                }
            }
            finally
            {
                // Always scrub the uncompressed file tree workspace footprint from storage when finalized
                Directory.Delete(_stagingDirectory, true);
            }
        }

        public void Dispose()
        {
            if (!string.IsNullOrEmpty(_stagingDirectory) && Directory.Exists(_stagingDirectory))
            {
                try { Directory.Delete(_stagingDirectory, true); } catch { }
            }
        }
    }
}
