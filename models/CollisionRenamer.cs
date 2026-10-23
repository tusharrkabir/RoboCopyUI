using System.Diagnostics;
using System.IO;

namespace RoboCopyUI
{
    // Robocopy has no "copy but rename if it already exists" flag — so
    // entries chosen for Rename are excluded from the main copy and handled
    // here instead: one at a time, into a uniquely suffixed destination name.
    // Runs synchronously BEFORE the main copy starts (on the same background
    // thread Start() already runs on) — not part of live telemetry, since
    // these are the exceptional few items that actually collided, not the
    // bulk of the job.
    public static class CollisionRenamer
    {
        public static void RenameAndCopy(CollisionEntry entry, RobocopyOptions options)
        {
            var uniqueDest = ReserveUniquePath(entry.DestFullPath);

            if (!entry.IsDirectory)
            {
                File.Copy(entry.SourceFullPath, uniqueDest, overwrite: false);
                return;
            }

            // Directory: a dedicated robocopy call straight into the unique
            // folder, reusing the same options (thread count, retry policy).
            var args = RobocopyCommandBuilder.BuildArguments(
                entry.SourceFullPath, uniqueDest, options, listOnly: false);

            var startInfo = new ProcessStartInfo
            {
                FileName = "robocopy.exe",
                Arguments = args,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            process.StandardOutput.ReadToEnd(); // drained, not parsed — not tracked
            process.WaitForExit();
        }

        private static string ReserveUniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;

            var dir = Path.GetDirectoryName(path);
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path); // empty for directories
            int suffix = 1;
            string candidate;

            do
            {
                candidate = Path.Combine(dir, $"{name} ({suffix}){ext}");
                suffix++;
            } while (File.Exists(candidate) || Directory.Exists(candidate));

            return candidate;
        }
    }
}