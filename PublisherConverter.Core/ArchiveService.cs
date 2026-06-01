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
        private bool _finalized;

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

            bool hasFiles = Directory.GetFiles(_stagingDirectory, "*", SearchOption.AllDirectories).Length > 0;

            if (_compress)
            {
                try
                {
                    if (hasFiles)
                    {
                        string finalZipPath = _stagingDirectory + ".zip";
                        ZipFile.CreateFromDirectory(_stagingDirectory, finalZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
                    }
                }
                finally
                {
                    Directory.Delete(_stagingDirectory, true); // scrub staging only once its contents are in the zip
                }
            }
            else if (!hasFiles)
            {
                Directory.Delete(_stagingDirectory, true);     // nothing staged -> remove empty placeholder
            }
            // else: uncompressed + has files -> KEEP the staging folder; it is the archive.

            _finalized = true;
        }

        public void Dispose()
        {
            // Only clean up an UN-finalized staging dir (i.e., on abandonment/error). A finalized
            // uncompressed archive must survive Dispose.
            if (!_finalized && !string.IsNullOrEmpty(_stagingDirectory) && Directory.Exists(_stagingDirectory))
            {
                try { Directory.Delete(_stagingDirectory, true); } catch { }
            }
        }
    }
}
