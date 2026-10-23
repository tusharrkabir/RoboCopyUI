using System;
using System.IO;

namespace RoboCopyUI
{
    public static class PathFormatter
    {
        /// <summary>
        /// Formats a file or directory path into a concise representation:
        /// Keeps the drive letter (e.g. C:\) and the target folder/file name,
        /// truncating middle directories or extremely long names so paths fit side by side.
        /// </summary>
        public static string FormatConcisePath(string path, int maxTotalLength = 26)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            string trimmed = path.Trim();
            if (trimmed.Length <= maxTotalLength)
                return trimmed;

            string root = Path.GetPathRoot(trimmed) ?? string.Empty;
            if (string.IsNullOrEmpty(root))
            {
                root = trimmed.Length >= 2 && trimmed[1] == ':' ? trimmed.Substring(0, 2) + "\\" : @"C:\";
            }
            else
            {
                root = root.TrimEnd('\\', '/') + "\\";
            }

            string name = Path.GetFileName(trimmed.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name))
                name = trimmed;

            // Maximum characters allowed for the target name after accounting for "C:\...\" (~7 chars)
            int prefixLen = root.Length + 4; // e.g. "C:\...\" is 7 chars
            int maxNameLen = Math.Max(6, maxTotalLength - prefixLen);

            string conciseName = name;
            if (name.Length > maxNameLen)
            {
                conciseName = name.Substring(0, Math.Max(1, maxNameLen - 3)) + "...";
            }

            return $"{root}...\\{conciseName}";
        }
    }
}
