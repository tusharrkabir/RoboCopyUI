using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace RoboCopyUI
{
    public class RoboClipboardData
    {
        public List<string> SourcePaths { get; set; } = new List<string>();
        public bool IsCutMode { get; set; } = false;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        // Backward compatibility helper
        public string SourcePath => SourcePaths.FirstOrDefault() ?? string.Empty;
    }

    public static class ClipboardManager
    {
        private static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RoboCopyUI");

        private static readonly string ClipboardFilePath = Path.Combine(AppDataDir, "clipboard.json");
        private const string MutexName = @"Global\RoboCopyUI_Clipboard_Mutex";

        public static void SetCopy(string path)
        {
            SetCopy(new[] { path });
        }

        public static void SetCopy(IEnumerable<string> paths)
        {
            SetClipboardInternal(paths, isCut: false);
        }

        public static void SetCut(string path)
        {
            SetCut(new[] { path });
        }

        public static void SetCut(IEnumerable<string> paths)
        {
            SetClipboardInternal(paths, isCut: true);
        }

        private static void SetClipboardInternal(IEnumerable<string> paths, bool isCut)
        {
            using var mutex = new Mutex(false, MutexName);
            bool acquired = false;
            try
            {
                acquired = mutex.WaitOne(3000);

                var existing = ReadDataFromFile();
                var now = DateTime.UtcNow;
                var newList = new List<string>();

                // Accumulate if invoked within 5.0 seconds (handles multi-select invocation from Windows Explorer)
                if (existing != null && existing.IsCutMode == isCut && (now - existing.Timestamp).TotalSeconds <= 5.0)
                {
                    newList.AddRange(existing.SourcePaths);
                }

                foreach (var p in paths)
                {
                    if (!string.IsNullOrWhiteSpace(p) && !newList.Contains(p, StringComparer.OrdinalIgnoreCase))
                    {
                        newList.Add(p);
                    }
                }

                SaveDataToFile(new RoboClipboardData
                {
                    SourcePaths = newList,
                    IsCutMode = isCut,
                    Timestamp = now
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SetClipboardInternal failed: {ex.Message}");
            }
            finally
            {
                if (acquired)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }

        public static RoboClipboardData? GetClipboard()
        {
            using var mutex = new Mutex(false, MutexName);
            bool acquired = false;
            try
            {
                acquired = mutex.WaitOne(3000);
                return ReadDataFromFile();
            }
            catch
            {
                return ReadDataFromFile();
            }
            finally
            {
                if (acquired)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }

        public static void Clear()
        {
            using var mutex = new Mutex(false, MutexName);
            bool acquired = false;
            try
            {
                acquired = mutex.WaitOne(3000);
                if (File.Exists(ClipboardFilePath))
                {
                    File.Delete(ClipboardFilePath);
                }
            }
            catch { }
            finally
            {
                if (acquired)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }

        private static RoboClipboardData? ReadDataFromFile()
        {
            try
            {
                if (File.Exists(ClipboardFilePath))
                {
                    string json = File.ReadAllText(ClipboardFilePath);
                    var data = JsonSerializer.Deserialize<RoboClipboardData>(json);
                    if (data != null && data.SourcePaths != null && data.SourcePaths.Count > 0)
                    {
                        return data;
                    }
                }
            }
            catch { }

            return null;
        }

        private static void SaveDataToFile(RoboClipboardData data)
        {
            try
            {
                if (!Directory.Exists(AppDataDir))
                {
                    Directory.CreateDirectory(AppDataDir);
                }

                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ClipboardFilePath, json);
            }
            catch { }
        }
    }
}
