susing System.IO;

namespace RoboCopyUI
{
    /// <summary>
    /// What to do about a file that already exists at the destination.
    /// Replace = let robocopy overwrite it normally (the default).
    /// Rename  = copy the source file in under a new, non-clashing name,
    ///           leaving the existing destination file untouched.
    /// Skip    = don't copy this file at all; the existing destination
    ///           file is left exactly as it is.
    /// </summary>
    public enum ConflictAction
    {
        Replace,
        Rename,
        Skip
    }

    /// <summary>
    /// One file that exists both at the source and at the destination,
    /// found by RobocopyEngine.PrepareJob()'s /L pre-scan — BEFORE any
    /// actual copying happens. MainWindow shows one row per entry in the
    /// conflict popup and sets .Action based on what the person picks;
    /// RobocopyEngine.BeginCopy() then reads .Action back to decide how
    /// to handle each file.
    /// </summary>
    public class FileConflict
    {
        public string SourcePath { get; }
        public string DestinationPath { get; }
        public long SizeBytes { get; }

        // Defaults to Replace (robocopy's own normal behavior) so a
        // conflict nobody explicitly decides on still does something sane.
        public ConflictAction Action { get; set; } = ConflictAction.Replace;

        public string FileName => Path.GetFileName(DestinationPath);
        public string SizeDisplay => CopyProgress.FormatBytes(SizeBytes);

        public FileConflict(string sourcePath, string destinationPath, long sizeBytes)
        {
            SourcePath = sourcePath;
            DestinationPath = destinationPath;
            SizeBytes = sizeBytes;
        }
    }
}
