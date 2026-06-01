using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace PublisherConverter.Core
{
    public class HashProvider : IHashProvider
    {
        public string GetSha256Hash(string filePath)
        {
            if (!File.Exists(filePath)) return "Missing";
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }

    public class ManifestWriter : IManifestWriter
    {
        public void WriteManifest(string directory, List<FileRecord> records)
        {
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            string csvPath = Path.Combine(directory, $"MigrationManifest_{DateTime.UtcNow:yyyyMMdd_HHmmss}Z.csv");
            using var writer = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8);
            writer.WriteLine("Timestamp,FileName,RelativeFolder,Status,SourceHash,OutputHash,HasMacros,MissingLinkCount,Details,LinkManifest");

            foreach (var r in records)
            {
                writer.WriteLine($"{ConverterEngine.CsvEscape(r.ProcessedAtUtc.ToString("yyyy-MM-dd HH:mm:ssZ"))}," +
                                 $"{ConverterEngine.CsvEscape(r.FileName)}," +
                                 $"{ConverterEngine.CsvEscape(r.RelativePath)}," +
                                 $"{ConverterEngine.CsvEscape(r.Status.ToString())}," +
                                 $"{ConverterEngine.CsvEscape(r.SourceHash)}," +
                                 $"{ConverterEngine.CsvEscape(r.OutputHash)}," +
                                 $"{r.HasMacros.ToString().ToLower()}," +
                                 $"{r.MissingAssetsCount}," +
                                 $"{ConverterEngine.CsvEscape(r.Details)}," +
                                 $"{ConverterEngine.CsvEscape(r.MissingAssetsList)}");
            }
        }
    }
}
