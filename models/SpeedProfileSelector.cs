namespace RoboCopyUI
{
    // Mirrors the auto-profile logic from other robocopy wrappers: picks
    // /MT based on file count and average/max file size from the scan
    // totals — no manual ThreadCount tuning needed.
    public static class SpeedProfileSelector
    {
        private const long MB = 1024 * 1024;
        private const long GB = 1024 * MB;
        private const long SmallFileBytes = 8 * MB;
        private const long LargeFileBytes = 256 * MB;

        public static (int ThreadCount, string ProfileName) Choose(
            long totalBytes, int totalFiles, long maxFileBytes, int smallFiles)
        {
            var files = System.Math.Max(1, totalFiles);
            var averageFileBytes = (double)totalBytes / files;
            var smallRatio = (double)smallFiles / files;

            if (files >= 5000 && (averageFileBytes <= 2 * MB || smallRatio >= 0.85))
                return (96, "Tiny File Turbo");

            if (files >= 500 && (averageFileBytes <= SmallFileBytes || smallRatio >= 0.7))
                return (64, "Small File Turbo");

            if (maxFileBytes >= GB || averageFileBytes >= LargeFileBytes)
                return (32, "Large File Stream");

            if (maxFileBytes >= LargeFileBytes || averageFileBytes >= 64 * MB)
                return (32, "Mixed Large Fast");

            return (32, "Balanced Fast");
        }
    }
}