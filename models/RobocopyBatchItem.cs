using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RoboCopyUI
{
    public class RobocopyBatchItem
    {
        public string SourceDir { get; set; } = string.Empty;
        public string DestDir { get; set; } = string.Empty;
        public List<string> FileSpecs { get; set; } = new List<string>();
        public bool IsDirectoryCopy { get; set; } = false;
        public string DisplayName { get; set; } = string.Empty;

        public static List<RobocopyBatchItem> CreateBatches(IEnumerable<string> sourcePaths, string destinationDir)
        {
            var batches = new List<RobocopyBatchItem>();
            var fileGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in sourcePaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                if (Directory.Exists(path))
                {
                    string folderName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (string.IsNullOrEmpty(folderName)) folderName = "Folder";

                    string targetSubDir = Path.Combine(destinationDir, folderName);

                    batches.Add(new RobocopyBatchItem
                    {
                        SourceDir = path,
                        DestDir = targetSubDir,
                        FileSpecs = new List<string> { "*.*" },
                        IsDirectoryCopy = true,
                        DisplayName = folderName
                    });
                }
                else if (File.Exists(path))
                {
                    string parentDir = Path.GetDirectoryName(path) ?? string.Empty;
                    string fileName = Path.GetFileName(path);

                    if (!string.IsNullOrEmpty(parentDir) && !string.IsNullOrEmpty(fileName))
                    {
                        if (!fileGroups.TryGetValue(parentDir, out var list))
                        {
                            list = new List<string>();
                            fileGroups[parentDir] = list;
                        }
                        if (!list.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                        {
                            list.Add(fileName);
                        }
                    }
                }
            }

            const int maxFilesPerBatch = 50;
            foreach (var kvp in fileGroups)
            {
                var files = kvp.Value;
                for (int i = 0; i < files.Count; i += maxFilesPerBatch)
                {
                    var chunk = files.Skip(i).Take(maxFilesPerBatch).ToList();
                    batches.Add(new RobocopyBatchItem
                    {
                        SourceDir = kvp.Key,
                        DestDir = destinationDir,
                        FileSpecs = chunk,
                        IsDirectoryCopy = false,
                        DisplayName = files.Count == 1 ? files[0] : $"{files.Count} files"
                    });
                }
            }

            return batches;
        }
    }
}

