using System;
using System.IO;
using System.IO.Compression;
using OpenMcdf;
using Xunit;
using PublisherConverter.Core;

namespace PublisherConverter.Tests
{
    public class InspectorTests : IDisposable
    {
        private readonly string _testWorkspaceDir;

        public InspectorTests()
        {
            _testWorkspaceDir = Path.Combine(Path.GetTempPath(), $"PubTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testWorkspaceDir);
        }

        // ==========================================
        // 1. Core Inspector Tests (Existing + Expanded)
        // ==========================================

        [Fact]
        public void InspectFile_ShouldReturn_CorruptedOrInvalid_ForNonOleFiles()
        {
            string fakePubPath = Path.Combine(_testWorkspaceDir, "plain_text.pub");
            File.WriteAllText(fakePubPath, "This is not an OLE compound file structure.");

            var result = PublisherInspector.InspectFile(fakePubPath);

            Assert.True(result.IsCorruptedOrInvalid);
            Assert.Contains("Invalid file layout", result.Reason);
        }

        [Fact]
        public void InspectFile_ShouldReturn_Clean_ForStandardEmptyOleContainer()
        {
            string cleanPubPath = Path.Combine(_testWorkspaceDir, "clean_container.pub");
            using (var cf = new CompoundFile())
            {
                cf.RootStorage.AddStream("Contents").SetData(new byte[] { 0x01, 0x02, 0x03 });
                cf.SaveAs(cleanPubPath);
            }

            var result = PublisherInspector.InspectFile(cleanPubPath);

            Assert.False(result.HasMacros);
            Assert.False(result.IsPasswordProtected);
            Assert.False(result.IsCorruptedOrInvalid);
            Assert.Equal("Clean", result.Reason);
        }

        [Fact]
        public void InspectFile_ShouldReturn_HasMacros_WhenVbaStorageStampsExist()
        {
            string macroPubPath = Path.Combine(_testWorkspaceDir, "macro_document.pub");
            using (var cf = new CompoundFile())
            {
                var vbaStorage = cf.RootStorage.AddStorage("VBA");
                vbaStorage.AddStream("dir").SetData(new byte[] { 0x00, 0x11, 0x22 });
                cf.SaveAs(macroPubPath);
            }

            var result = PublisherInspector.InspectFile(macroPubPath);

            Assert.True(result.HasMacros);
            Assert.Contains("contains active VBA macros", result.Reason);
        }

        [Fact]
        public async void GetSha256Hash_ShouldGenerate_ConsistentCryptographicSignatures()
        {
            string targetFile = Path.Combine(_testWorkspaceDir, "hash_test.txt");
            await File.WriteAllTextAsync(targetFile, "East Central Regional Library System Migration 2026");

            string hashFirstPass = ConverterEngine.GetSha256Hash(targetFile);
            string hashSecondPass = ConverterEngine.GetSha256Hash(targetFile);

            Assert.Equal(64, hashFirstPass.Length);
            Assert.Equal(hashFirstPass, hashSecondPass);
        }

        [Fact]
        public void InspectFile_ShouldReturn_Invalid_WhenFileDoesNotExist()
        {
            // Arrange: Generate a path pointing to a completely imaginary file
            string nonExistentPath = Path.Combine(_testWorkspaceDir, "ghost_file.pub");

            // Act: Attempt triage
            var result = PublisherInspector.InspectFile(nonExistentPath);

            // Assert: Verify the engine flags it gracefully before wasting resources
            Assert.True(result.IsCorruptedOrInvalid);
            Assert.Contains("does not exist", result.Reason.ToLowerInvariant());
        }

        [Fact]
        public void InspectFile_ShouldReturn_HasMacros_WhenAlternateVbaStampExists()
        {
            // Arrange: Synthesize an OLE file targeting the secondary alternate legacy Office macro registry stamp
            string legacyMacroPath = Path.Combine(_testWorkspaceDir, "legacy_macro.pub");
            using (var cf = new CompoundFile())
            {
                var legacyStorage = cf.RootStorage.AddStorage("_VBA_PROJECT_CUR");
                legacyStorage.AddStream("Project").SetData(new byte[] { 0xAA, 0xBB });
                cf.SaveAs(legacyMacroPath);
            }

            // Act: Triage the document
            var result = PublisherInspector.InspectFile(legacyMacroPath);

            // Assert: Confirm the engine catches both modern and legacy structural layouts
            Assert.True(result.HasMacros);
            Assert.Contains("contains active VBA macros", result.Reason);
        }

        // ==========================================
        // 2. Pure Archive Service Verification Tests
        // ==========================================

        [Fact]
        public void ArchiveService_ShouldMaintainNestedDirectoryTreesAndZipCleanly()
        {
            // Arrange: Configure an archive testing target pipeline workspace
            string backupTargetDir = Path.Combine(_testWorkspaceDir, "BackupVault");
            string sourceFileFake = Path.Combine(_testWorkspaceDir, "original_template.pub");
            File.WriteAllText(sourceFileFake, "Fake Publisher Binary Stream Content Context Data Block");

            var archiveSvc = new ArchiveService(backupTargetDir);

            // Act - Part A: Initialize backup staging spaces
            archiveSvc.Initialize();

            // Capture the dynamically generated timestamped staging directory name to inspect it
            string[] stagingDirsBefore = Directory.GetDirectories(backupTargetDir, "PublisherBackup_*");
            Assert.Single(stagingDirsBefore); // Prove exactly 1 folder was built
            string dynamicStagingPath = stagingDirsBefore[0];

            // Act - Part B: Stage an asset deep inside a relative subfolder path layout
            string nestedRelativePath = Path.Combine("Marketing", "Newsletters", "SummerPass");;
            archiveSvc.StageFile(sourceFileFake, nestedRelativePath, "summer_draft.pub");

            // Assert - Part B: Verify directory structure replication
            string expectedStagedFilePath = Path.Combine(dynamicStagingPath, "Marketing", "Newsletters", "SummerPass", "summer_draft.pub");
            Assert.True(File.Exists(expectedStagedFilePath), "Staged file was not correctly duplicated into the relative tree layout.");

            // Act - Part C: Compile ZIP container and execute raw scratch scrubbing
            archiveSvc.FinalizeArchive();

            // Assert - Part C: Confirm uncompressed staging footprint is gone and replaced by an immutable ZIP
            Assert.False(Directory.Exists(dynamicStagingPath), "Staging workspace directory was not wiped out following compilation pass.");

            string expectedZipPath = dynamicStagingPath + ".zip";
            Assert.True(File.Exists(expectedZipPath), "The final compressed archival ZIP package file container is missing.");

            // Verify the integrity of the folder structure inside the ZIP archive using ZipArchive inspection
            using (ZipArchive zip = ZipFile.OpenRead(expectedZipPath))
            {
                // Zip paths always flatten slashes to forward slashes
                Assert.NotNull(zip.GetEntry("Marketing/Newsletters/SummerPass/summer_draft.pub"));
            }
        }

        [Fact]
        public void ArchiveService_ShouldNotCreateZip_IfZeroFilesWereStaged()
        {
            // Arrange: Setup an empty pipeline execution sequence test
            string backupTargetDir = Path.Combine(_testWorkspaceDir, "EmptyVault");
            var archiveSvc = new ArchiveService(backupTargetDir);

            // Act: Initialize and immediately finalize without staging anything
            archiveSvc.Initialize();

            string[] stagingDirs = Directory.GetDirectories(backupTargetDir, "PublisherBackup_*");
            string dynamicStagingPath = stagingDirs[0];

            archiveSvc.FinalizeArchive();

            // Assert: Staging is safely scrubbed, but no empty placeholder zip was created
            Assert.False(Directory.Exists(dynamicStagingPath));
            Assert.False(File.Exists(dynamicStagingPath + ".zip"), "An archive ZIP was created even though zero documents were successfully processed.");
        }

        public void Dispose()
        {
            if (Directory.Exists(_testWorkspaceDir))
            {
                try { Directory.Delete(_testWorkspaceDir, true); } catch { }
            }
        }
    }
}