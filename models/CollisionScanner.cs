using System.Collections.Generic;
using System.IO;

namespace RoboCopyUI
{
    public static class CollisionScanner
    {
        public static List<CollisionEntry> FindCollisions(RobocopyBatchItem batch)
        {
            var result = new List<CollisionEntry>();

            if (batch.IsDirectoryCopy)
            {
                // 1. If target directory itself exists
                if (Directory.Exists(batch.DestDir))
                {
                    string folderName = Path.GetFileName(batch.DestDir.TrimEnd('\\', '/'));
                    if (string.IsNullOrEmpty(folderName)) folderName = batch.DisplayName;

                    result.Add(new CollisionEntry
                    {
                        Name = folderName,
                        IsDirectory = true,
                        SourceFullPath = batch.SourceDir,
                        DestFullPath = batch.DestDir
                    });
                }
                else
                {
                    // 2. Check top-level items inside source vs parent destination
                    if (Directory.Exists(batch.SourceDir))
                    {
                        var parentDest = Path.GetDirectoryName(batch.DestDir.TrimEnd('\\', '/')) ?? batch.DestDir;
                        if (Directory.Exists(parentDest))
                        {
                            result.AddRange(FindTopLevelCollisions(batch.SourceDir, parentDest));
                        }
                    }
                }
            }
            else
            {
                // File batch: check each file spec in destination directory
                if (Directory.Exists(batch.DestDir))
                {
                    foreach (var spec in batch.FileSpecs)
                    {
                        string sourceFile = Path.Combine(batch.SourceDir, spec);
                        string destFile = Path.Combine(batch.DestDir, spec);

                        if (File.Exists(destFile) || Directory.Exists(destFile))
                        {
                            result.Add(new CollisionEntry
                            {
                                Name = spec,
                                IsDirectory = Directory.Exists(destFile),
                                SourceFullPath = sourceFile,
                                DestFullPath = destFile
                            });
                        }
                    }
                }
            }

            return result;
        }

        public static List<CollisionEntry> FindTopLevelCollisions(string source, string destination)
        {
            var result = new List<CollisionEntry>();
            if (!Directory.Exists(source) || !Directory.Exists(destination))
                return result;

            try
            {
                foreach (var dir in Directory.GetDirectories(source))
                {
                    var name = Path.GetFileName(dir);
                    var destPath = Path.Combine(destination, name);
                    if (Directory.Exists(destPath))
                    {
                        result.Add(new CollisionEntry
                        {
                            Name = name,
                            IsDirectory = true,
                            SourceFullPath = dir,
                            DestFullPath = destPath
                        });
                    }
                }

                foreach (var file in Directory.GetFiles(source))
                {
                    var name = Path.GetFileName(file);
                    var destPath = Path.Combine(destination, name);
                    if (File.Exists(destPath))
                    {
                        result.Add(new CollisionEntry
                        {
                            Name = name,
                            IsDirectory = false,
                            SourceFullPath = file,
                            DestFullPath = destPath
                        });
                    }
                }
            }
            catch { }

            return result;
        }
    }
}