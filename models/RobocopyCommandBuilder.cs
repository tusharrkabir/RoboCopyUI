using System.Collections.Generic;
using System.Linq;

namespace RoboCopyUI
{
    public static class RobocopyCommandBuilder
    {
        public static string FormatRobocopyPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "\"\"";
            string fullPath = System.IO.Path.GetFullPath(path).TrimEnd('\\') + "\\";
            // Ensure trailing backslash escapes itself when quoted
            return $"\"{fullPath}\\\"";
        }

        public static string BuildArguments(
            RobocopyBatchItem batch, RobocopyOptions options, bool listOnly,
            List<CollisionEntry>? excludedEntries = null, bool forceOverwriteSame = false)
        {
            string fileSpecs = (batch.FileSpecs != null && batch.FileSpecs.Count > 0)
                ? string.Join(" ", batch.FileSpecs.Select(f => $"\"{f}\""))
                : $"\"{options.FileMask}\"";

            // Crucial: Only apply recursive /E or /MIR flags for directory copies.
            // File copies must NOT include /E or /MIR to prevent copying unrelated files or desktop.ini.
            string flags = batch.IsDirectoryCopy ? (options.Mirror ? "/MIR" : "/E") : "";

            string sourceArg = FormatRobocopyPath(batch.SourceDir);
            string destArg = FormatRobocopyPath(batch.DestDir);

            var args = $"{sourceArg} {destArg} {fileSpecs} " +
                       $"{flags} /BYTES /FP /NJH /NJS /NDL /NC /NP /FFT /IPG:0 " +
                       $"/R:{options.RetryCount} /W:{options.WaitSeconds} /MT:{options.ThreadCount}";

            if (options.UnbufferedIO)
                args += " /J";

            if (options.PreserveTimestamps)
                args += " /DCOPY:T"; // T only copies timestamps. Prevents copying hidden/system attributes to destination folders.
            else
                args += " /NODCOPY"; // Disables directory info copying (prevents default DA which copies attributes).

            if (forceOverwriteSame)
                args += " /IS /IT";

            if (excludedEntries != null && excludedEntries.Count > 0)
            {
                var files = excludedEntries.Where(e => !e.IsDirectory).Select(e => $"\"{e.Name}\"").ToList();
                var dirs = excludedEntries.Where(e => e.IsDirectory).Select(e => $"\"{e.Name}\"").ToList();

                if (files.Count > 0) args += $" /XF {string.Join(" ", files)}";
                if (dirs.Count > 0) args += $" /XD {string.Join(" ", dirs)}";
            }

            if (listOnly)
                args += " /L";

            return args;
        }

        public static string BuildArguments(
            string source, string destination, RobocopyOptions options, bool listOnly,
            List<CollisionEntry>? excludedEntries = null, bool forceOverwriteSame = false)
        {
            bool isDir = System.IO.Directory.Exists(source);
            var batch = new RobocopyBatchItem
            {
                SourceDir = isDir ? source : (System.IO.Path.GetDirectoryName(source) ?? source),
                DestDir = destination,
                FileSpecs = isDir ? new List<string> { options.FileMask } : new List<string> { System.IO.Path.GetFileName(source) },
                IsDirectoryCopy = isDir
            };

            return BuildArguments(batch, options, listOnly, excludedEntries, forceOverwriteSame);
        }
    }
}
