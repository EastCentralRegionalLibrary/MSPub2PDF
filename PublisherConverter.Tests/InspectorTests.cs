using System;
using System.IO;
using OpenMcdf;
using Xunit;
using PublisherConverter.Core;

namespace PublisherConverter.Tests
{
    public class InspectorTests : IDisposable
    {
        private readonly string _testWorkspaceDir;

        // The Constructor runs BEFORE every single individual test case to prepare a safe workspace
        public InspectorTests()
        {
            _testWorkspaceDir = Path.Combine(Path.GetTempPath(), $"PubTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testWorkspaceDir);
        }

        [Fact]
        public void InspectFile_ShouldReturn_CorruptedOrInvalid_ForNonOleFiles()
        {
            // Arrange: Write a plain text file pretending to be a publisher file
            string fakePubPath = Path.Combine(_testWorkspaceDir, "plain_text.pub");
            File.WriteAllText(fakePubPath, "This is not an OLE compound file structure.");

            // Act: Fire the inspector backend
            var result = PublisherInspector.InspectFile(fakePubPath);

            // Assert: Verify the file is flagged as structurally invalid
            Assert.True(result.IsCorruptedOrInvalid);
            Assert.Contains("Invalid file layout", result.Reason);
        }

        [Fact]
        public void InspectFile_ShouldReturn_Clean_ForStandardEmptyOleContainer()
        {
            // Arrange: Build a standard, completely clean OLE compound file structure
            string cleanPubPath = Path.Combine(_testWorkspaceDir, "clean_container.pub");
            using (var cf = new CompoundFile())
            {
                cf.RootStorage.AddStream("Contents").SetData(new byte[] { 0x01, 0x02, 0x03 });
                cf.SaveAs(cleanPubPath);
            }

            // Act: Evaluate container
            var result = PublisherInspector.InspectFile(cleanPubPath);

            // Assert: Ensure it passes triage with flying colors
            Assert.False(result.HasMacros);
            Assert.False(result.IsPasswordProtected);
            Assert.False(result.IsCorruptedOrInvalid);
            Assert.Equal("Clean", result.Reason);
        }

        [Fact]
        public void InspectFile_ShouldReturn_HasMacros_WhenVbaStorageStampsExist()
        {
            // Arrange: Synthesize an OLE file containing a malicious/problematic hidden "VBA" directory
            string macroPubPath = Path.Combine(_testWorkspaceDir, "macro_document.pub");
            using (var cf = new CompoundFile())
            {
                // Create the exact signature directory block our inspector watches for
                var vbaStorage = cf.RootStorage.AddStorage("VBA");
                vbaStorage.AddStream("dir").SetData(new byte[] { 0x00, 0x11, 0x22 });
                cf.SaveAs(macroPubPath);
            }

            // Act: Run pre-flight inspect pass
            var result = PublisherInspector.InspectFile(macroPubPath);

            // Assert: Confirm the engine successfully catches the hidden script panel
            Assert.True(result.HasMacros);
            Assert.Contains("contains active VBA macros", result.Reason);
        }

        [Fact]
        public void GetSha256Hash_ShouldGenerate_ConsistentCryptographicSignatures()
        {
            // Arrange: Generate a static temporary asset item
            string targetFile = Path.Combine(_testWorkspaceDir, "hash_test.txt");
            File.WriteAllText(targetFile, "East Central Regional Library System Migration 2026");

            // Act: Compute signature metrics
            string hashFirstPass = ConverterEngine.GetSha256Hash(targetFile);
            string hashSecondPass = ConverterEngine.GetSha256Hash(targetFile);

            // Assert: Verify SHA-256 calculation outputs are lowercase, structured, and consistent
            Assert.Equal(64, hashFirstPass.Length);
            Assert.Equal(hashFirstPass, hashSecondPass); // Must match exactly
        }

        // The Dispose method runs AFTER every single test case to scrub the computer's storage paths
        public void Dispose()
        {
            if (Directory.Exists(_testWorkspaceDir))
            {
                Directory.Delete(_testWorkspaceDir, true);
            }
        }
    }
}