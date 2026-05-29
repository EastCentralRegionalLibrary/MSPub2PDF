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

    public static class PublisherInspector
    {
        public static FileSafetyStatus InspectFile(string filePath)
        {
            var status = new FileSafetyStatus();

            if (!File.Exists(filePath))
            {
                status.IsCorruptedOrInvalid = true;
                status.Reason = "File does not exist at specified path.";
                return status;
            }

            // FIX 2: Explicitly catch zero-byte files before parsing headers
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
                        // FIX 3: Short-circuit string matching if a signature has already been flagged
                        if (macroFound || encryptionFound) return;

                        string name = item.Name;

                        // Check for embedded VBA scripts
                        if (name.Equals("VBA", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("_VBA_PROJECT_CUR", StringComparison.OrdinalIgnoreCase))
                        {
                            macroFound = true;
                            return;
                        }

                        // FIX 1: Proactively flag standard uncompressed OLE password envelopes
                        if (name.Equals("EncryptionInfo", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("EncryptedPackage", StringComparison.OrdinalIgnoreCase))
                        {
                            encryptionFound = true;
                        }
                    }, true); // Recursive scan

                    // Evaluate our structural findings
                    if (encryptionFound)
                    {
                        status.IsPasswordProtected = true;
                        status.Reason = "File is password protected (Contains standard OLE encryption headers).";
                        return status;
                    }

                    if (macroFound)
                    {
                        status.HasMacros = true;
                        status.Reason = "Document contains active VBA macros.";
                        return status;
                    }
                }
            }
            // Catch hard-encrypted OLE container walls where parsing headers fails immediately
            catch (CFException ex) when
                (ex.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                )
            {
                status.IsPasswordProtected = true;
                status.Reason = "File is password protected or encrypted.";
            }
            // Catch structural format breaks and corrupted sector tables
            catch (CFException)
            {
                status.IsCorruptedOrInvalid = true;
                status.Reason = "Invalid file layout (Not a standard .pub binary container or corrupt).";
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