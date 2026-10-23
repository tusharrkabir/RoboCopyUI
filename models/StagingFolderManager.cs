using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace RoboCopyUI
{
    public static class StagingFolderManager
    {
        public static string CreateStagingFolder(IEnumerable<string> sourcePaths)
        {
            string tempPath = Path.GetTempPath();
            string stagingDir = Path.Combine(tempPath, $"RoboCopyUI_Staging_{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDir);

            foreach (var path in sourcePaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) name = "Item";

                string targetLinkPath = Path.Combine(stagingDir, name);

                if (Directory.Exists(path))
                {
                    CreateDirectoryJunction(targetLinkPath, Path.GetFullPath(path));
                }
                else if (File.Exists(path))
                {
                    CreateFileLinkOrCopy(targetLinkPath, Path.GetFullPath(path));
                }
            }

            return stagingDir;
        }

        private static void CreateDirectoryJunction(string junctionPath, string targetPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }

        private static void CreateFileLinkOrCopy(string linkPath, string targetPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /H \"{linkPath}\" \"{targetPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();

            if (!File.Exists(linkPath))
            {
                try
                {
                    File.Copy(targetPath, linkPath, overwrite: true);
                }
                catch { }
            }
        }

        public static void CleanupStagingFolder(string? stagingDir)
        {
            if (string.IsNullOrEmpty(stagingDir) || !Directory.Exists(stagingDir)) return;

            try
            {
                var dirInfo = new DirectoryInfo(stagingDir);

                foreach (var file in dirInfo.GetFiles())
                {
                    try { file.Delete(); } catch { }
                }

                foreach (var subDir in dirInfo.GetDirectories())
                {
                    // Non-recursive delete of junction point link ensures original target folder is untouched
                    try { subDir.Delete(recursive: false); } catch { }
                }

                Directory.Delete(stagingDir, recursive: false);
            }
            catch { }
        }
    }
}
