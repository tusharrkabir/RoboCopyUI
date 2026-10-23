using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace RoboCopyUI
{
    public class ScanResult
    {
        public long TotalBytes;
        public int TotalFiles;
        public long MaxFileBytes;   // needed by SpeedProfileSelector
        public int SmallFiles;      // needed by SpeedProfileSelector (<= 8MB)
        public PendingFileTable Pending = new PendingFileTable();
        public Dictionary<string, long> FilesToCopy = new Dictionary<string, long>(System.StringComparer.OrdinalIgnoreCase);
    }

    public static class ScanService
    {
        private const long SmallFileBytes = 8 * 1024 * 1024;

        public static ScanResult Scan(
            RobocopyBatchItem batch, RobocopyOptions options,
            List<CollisionEntry>? excludeTopLevel = null, bool forceOverwriteSame = false,
            Func<bool>? isCancelled = null)
        {
            var result = new ScanResult();

            var startInfo = new ProcessStartInfo
            {
                FileName = "robocopy.exe",
                Arguments = RobocopyCommandBuilder.BuildArguments(
                    batch, options, listOnly: true, excludeTopLevel, forceOverwriteSame),
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var scanProcess = new Process { StartInfo = startInfo };
            scanProcess.Start();

            string? line;
            while ((line = scanProcess.StandardOutput.ReadLine()) != null)
            {
                if (isCancelled?.Invoke() == true)
                {
                    try { scanProcess.Kill(true); } catch { }
                    break;
                }

                var parsed = RobocopyLineParser.ParseFileLine(line);
                if (parsed == null || parsed.Value.isDirectory ||
                    System.Array.IndexOf(RobocopyLineParser.SkipTags, parsed.Value.tag) >= 0) continue;

                var path = parsed.Value.path;
                var size = parsed.Value.size;

                result.TotalBytes += size;
                result.TotalFiles++;
                if (size > result.MaxFileBytes) result.MaxFileBytes = size;
                if (size <= SmallFileBytes) result.SmallFiles++;

                var relativePath = PathUtil.MakeRelative(batch.SourceDir, path);
                var sourceFullPath = System.IO.Path.IsPathRooted(path)
                    ? System.IO.Path.GetFullPath(path)
                    : System.IO.Path.GetFullPath(System.IO.Path.Combine(batch.SourceDir, relativePath));

                var mtime = System.IO.File.Exists(sourceFullPath)
                    ? System.IO.File.GetLastWriteTimeUtc(sourceFullPath)
                    : default;

                result.Pending.Add(relativePath, new PendingEntry
                {
                    ExpectedSize = size,
                    ExpectedMTimeUtc = mtime,
                    DestinationFullPath = System.IO.Path.Combine(batch.DestDir, relativePath)
                });
                result.FilesToCopy[sourceFullPath] = size;
            }

            scanProcess.WaitForExit();
            return result;
        }

        public static ScanResult Scan(
            string source, string destination, RobocopyOptions options,
            List<CollisionEntry>? excludeTopLevel = null, bool forceOverwriteSame = false,
            Func<bool>? isCancelled = null)
        {
            bool isDir = System.IO.Directory.Exists(source);
            var batch = new RobocopyBatchItem
            {
                SourceDir = isDir ? source : (System.IO.Path.GetDirectoryName(source) ?? source),
                DestDir = destination,
                FileSpecs = isDir ? new List<string> { options.FileMask } : new List<string> { System.IO.Path.GetFileName(source) },
                IsDirectoryCopy = isDir
            };

            return Scan(batch, options, excludeTopLevel, forceOverwriteSame, isCancelled);
        }
    }

    internal static class PathUtil
    {
        public static string MakeRelative(string root, string fullPath)
        {
            var rootNorm = root.TrimEnd('\\') + "\\";
            return fullPath.StartsWith(rootNorm, System.StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(rootNorm.Length)
                : fullPath;
        }
    }
}
