using System;
using System.IO;
using System.IO.Compression;

namespace PublisherConverter.Core
{
    public class ArchiveService
    {
        private readonly string _targetBackupRoot;
        private string? _stagingDirectory;

        public ArchiveService(string targetBackupRoot)
        {
            _targetBackupRoot = targetBackupRoot;
        }

        /// <summary>
        /// Instantiates a unique, timestamped backup directory block inside the target repository layout.
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
        /// Carbon-copies an asset item while perfectly maintaining its original relative subfolder tree structures.
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
        /// Packs the temporary workspace directory structures into a single compressed ZIP package and cleans up staging footprints.
        /// </summary>
        public void FinalizeArchive()
        {
            if (string.IsNullOrEmpty(_stagingDirectory) || !Directory.Exists(_stagingDirectory)) return;

            try
            {
                // Verify if any items were successfully staged before pulling the ZIP triggers
                if (Directory.GetFiles(_stagingDirectory, "*", SearchOption.AllDirectories).Length > 0)
                {
                    string finalZipPath = _stagingDirectory + ".zip";
                    ZipFile.CreateFromDirectory(_stagingDirectory, finalZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
                }
            }
            finally
            {
                // Always scrub the uncompressed file tree workspace footprint from storage
                Directory.Delete(_stagingDirectory, true);
            }
        }
    }
}