using System;
using System.IO;
using OpenMcdf;

namespace PublisherConverter.Core
{
    public class FileSafetyStatus
    {
        public bool HasMacros { get; set; } = false;
        public bool IsPasswordProtected { get; set; } = false;
        public bool IsCorruptedOrInvalid { get; set; } = false;
        public string Reason { get; set; } = "Clean";
    }

    public class PublisherInspector : IFileInspector
    {
        public FileSafetyStatus InspectFile(string filePath)
        {
            var status = new FileSafetyStatus();

            if (!File.Exists(filePath))
            {
                status.IsCorruptedOrInvalid = true;
                status.Reason = "File does not exist at specified path.";
                return status;
            }

            // Explicitly catch zero-byte files before parsing headers
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length == 0)
            {
                status.IsCorruptedOrInvalid = true;
                status.Reason = "File is empty (Contains 0 bytes of data).";
                return status;
            }

            try
            {
                // Open the Compound File in a strictly read-only, shared mode
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var cf = new CompoundFile(stream))
                {
                    bool macroFound = false;
                    bool encryptionFound = false;

                    // Traverse internal structural entries safely using a callback delegate
                    cf.RootStorage.VisitEntries(item =>
                    {
                        // Short-circuit string matching if a signature has already been flagged
                        if (macroFound && encryptionFound) return;

                        string name = item.Name;

                        // Check for embedded VBA scripts
                        if (!macroFound && (name.Equals("VBA", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("_VBA_PROJECT_CUR", StringComparison.OrdinalIgnoreCase)))
                        {
                            macroFound = true;
                        }

                        // Flag standard OLE encryption headers
                        if (!encryptionFound && (name.Equals("EncryptionInfo", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("EncryptedPackage", StringComparison.OrdinalIgnoreCase)))
                        {
                            encryptionFound = true;
                        }
                    }, true); // Recursive scan

                    // Evaluate our structural findings
                    if (encryptionFound)
                    {
                        status.IsPasswordProtected = true;
                        status.Reason = "File is password protected (Contains standard OLE encryption headers).";
                    }

                    if (macroFound)
                    {
                        status.HasMacros = true;
                        if (!encryptionFound)
                        {
                            status.Reason = "Document contains active VBA macros.";
                        }
                    }

                    if (macroFound || encryptionFound) return status;
                }
            }
            // Catch structural format breaks, corrupted sector tables, or hard-encrypted OLE containers
            catch (CFException)
            {
                status.IsCorruptedOrInvalid = true;
                status.Reason = "Invalid file layout or corrupted OLE container.";
            }
            // Catch remaining unexpected operating system file access or hardware reading barriers
            catch (Exception ex)
            {
                status.IsCorruptedOrInvalid = true;
                status.Reason = $"Structural Parsing Error: {ex.Message}";
            }

            return status;
        }
    }
}
