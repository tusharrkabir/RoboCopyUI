using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RoboCopyUI
{
    /// <summary>
    /// Pre-scan result containing files that will actually be copied and their sizes.
    /// </summary>
    public class PreScanResult
    {
        public Dictionary<string, long> FilesToCopy { get; set; } = new();
        public long TotalBytesToCopy { get; set; }
        public int TotalFilesToCopy { get; set; }
    }

    /// <summary>
    /// Performs a detailed pre-scan to determine exactly which files will be copied
    /// and their exact sizes, enabling accurate byte-based progress tracking.
    /// </summary>
    public static class PreScanService
    {
        public static PreScanResult ScanFilesToCopy(RobocopyBatchItem batch,
            RobocopyOptions? options = null, List<CollisionEntry>? excludeFromCopy = null,
            bool forceOverwriteSame = false, Func<bool>? isCancelled = null)
        {
            options ??= new RobocopyOptions();
            excludeFromCopy ??= new List<CollisionEntry>();

            var result = new PreScanResult();
            var excludeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in excludeFromCopy)
            {
                if (!string.IsNullOrEmpty(entry.Name))
                    excludeSet.Add(Path.GetFullPath(Path.Combine(batch.SourceDir, entry.Name)));
                if (!string.IsNullOrEmpty(entry.SourceFullPath))
                    excludeSet.Add(Path.GetFullPath(entry.SourceFullPath));
                if (!string.IsNullOrEmpty(entry.DestFullPath))
                    excludeSet.Add(Path.GetFullPath(entry.DestFullPath));
            }

            try
            {
                if (batch.IsDirectoryCopy)
                {
                    ScanDirectory(batch.SourceDir, batch.DestDir, batch.SourceDir, options, excludeSet, forceOverwriteSame, result, isCancelled);
                }
                else
                {
                    foreach (var spec in batch.FileSpecs)
                    {
                        if (isCancelled?.Invoke() == true) break;

                        string sourceFile = Path.Combine(batch.SourceDir, spec);
                        string destFile = Path.Combine(batch.DestDir, spec);

                        if (excludeSet.Contains(Path.GetFullPath(sourceFile)) ||
                            excludeSet.Contains(Path.GetFullPath(destFile)))
                        {
                            continue;
                        }

                        if (File.Exists(sourceFile))
                        {
                            if (ShouldCopyFile(sourceFile, destFile, options, forceOverwriteSame))
                            {
                                var fileInfo = new FileInfo(sourceFile);
                                result.FilesToCopy[Path.GetFullPath(sourceFile)] = fileInfo.Length;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Pre-scan failed: {ex.Message}", ex);
            }

            result.TotalBytesToCopy = result.FilesToCopy.Values.Sum();
            result.TotalFilesToCopy = result.FilesToCopy.Count;

            return result;
        }

        public static PreScanResult ScanFilesToCopy(string sourcePath, string destinationPath,
            RobocopyOptions? options = null, List<CollisionEntry>? excludeFromCopy = null,
            bool forceOverwriteSame = false, Func<bool>? isCancelled = null)
        {
            bool isDir = Directory.Exists(sourcePath);
            var batch = new RobocopyBatchItem
            {
                SourceDir = isDir ? sourcePath : (Path.GetDirectoryName(sourcePath) ?? sourcePath),
                DestDir = destinationPath,
                FileSpecs = isDir ? new List<string> { (options?.FileMask ?? "*.*") } : new List<string> { Path.GetFileName(sourcePath) },
                IsDirectoryCopy = isDir
            };

            return ScanFilesToCopy(batch, options, excludeFromCopy, forceOverwriteSame, isCancelled);
        }

        private static void ScanDirectory(string currentSourceDir, string currentDestDir,
            string rootSourceDir, RobocopyOptions options, HashSet<string> excludeSet,
            bool forceOverwriteSame, PreScanResult result, Func<bool>? isCancelled)
        {
            if (isCancelled?.Invoke() == true || !Directory.Exists(currentSourceDir))
                return;

            try
            {
                var files = Directory.GetFiles(currentSourceDir, options.FileMask);
                foreach (var sourceFile in files)
                {
                    if (isCancelled?.Invoke() == true) return;

                    if (excludeSet.Contains(Path.GetFullPath(sourceFile)))
                        continue;

                    var relativePath = Path.GetRelativePath(rootSourceDir, sourceFile);
                    var destFile = Path.Combine(currentDestDir, Path.GetFileName(sourceFile));

                    if (excludeSet.Contains(Path.GetFullPath(destFile)))
                        continue;

                    if (ShouldCopyFile(sourceFile, destFile, options, forceOverwriteSame))
                    {
                        var fileInfo = new FileInfo(sourceFile);
                        result.FilesToCopy[Path.GetFullPath(sourceFile)] = fileInfo.Length;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            try
            {
                var directories = Directory.GetDirectories(currentSourceDir);
                foreach (var sourceSubDir in directories)
                {
                    if (isCancelled?.Invoke() == true) return;

                    if (excludeSet.Contains(Path.GetFullPath(sourceSubDir)))
                        continue;

                    var dirName = Path.GetFileName(sourceSubDir);
                    var destSubDir = Path.Combine(currentDestDir, dirName);

                    if (excludeSet.Contains(Path.GetFullPath(destSubDir)))
                        continue;

                    ScanDirectory(sourceSubDir, destSubDir, rootSourceDir, options, excludeSet, forceOverwriteSame, result, isCancelled);
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static bool ShouldCopyFile(string sourceFile, string destFile, RobocopyOptions options, bool forceOverwriteSame)
        {
            if (!File.Exists(destFile))
                return true;

            if (options.Mirror || forceOverwriteSame)
                return true;

            try
            {
                var sourceInfo = new FileInfo(sourceFile);
                var destInfo = new FileInfo(destFile);

                if (sourceInfo.Length != destInfo.Length)
                    return true;

                if (Math.Abs((sourceInfo.LastWriteTimeUtc - destInfo.LastWriteTimeUtc).TotalSeconds) > 2)
                    return true;

                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}