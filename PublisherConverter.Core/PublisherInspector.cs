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

            try
            {
                // Open the Compound File in a strictly read-only, shared mode
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var cf = new CompoundFile(stream))
                {
                    bool macroFound = false;

                    // Traverse internal structural entries safely using a callback delegate
                    cf.RootStorage.VisitEntries(item =>
                    {
                        if (item.Name.Equals("VBA", StringComparison.OrdinalIgnoreCase) ||
                            item.Name.Equals("_VBA_PROJECT_CUR", StringComparison.OrdinalIgnoreCase))
                        {
                            macroFound = true;
                        }
                    }, true); // Recursive scan

                    if (macroFound)
                    {
                        status.HasMacros = true;
                        status.Reason = "Document contains active VBA macros.";
                    }
                }
            }
            catch (CFException ex) when (ex.Message.Contains
            ("encrypted", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains
            ("password", StringComparison.OrdinalIgnoreCase))
            {
                // OpenMcdf throws an internal file exception if structural sectors are locked by encryption
                status.IsPasswordProtected = true;
                status.Reason = "File is password protected or encrypted.";
            }
            catch (CFException)
            {
                status.IsCorruptedOrInvalid = true;
                status.Reason = "Invalid file layout (Not a standard .pub binary container or corrupt).";
            }
            catch (Exception ex)
            {
                // Catch any remaining unexpected file or read barriers
                status.IsCorruptedOrInvalid = true;
                status.Reason = $"Structural Parsing Error: {ex.Message}";
            }

            return status;
        }
    }
}