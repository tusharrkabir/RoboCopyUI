using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace RoboCopyUI
{
    public class DeleteSessionData
    {
        public List<string> Paths { get; set; } = new List<string>();
        public DateTime CreatedTime { get; set; } = DateTime.UtcNow;
    }

    public static class DeleteSessionManager
    {
        private static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RoboCopyUI");

        private static readonly string SessionFilePath = Path.Combine(AppDataDir, "delete_session.json");
        private const string MutexName = @"Global\RoboCopyUI_Delete_Mutex";

        /// <summary>
        /// Registers the current instance's paths.
        /// If this is a secondary instance spawned by Windows Explorer in a multi-select batch,
        /// it appends its paths to the session file and returns isMaster=false (calling code should Shutdown).
        /// If this is the master instance, it waits briefly for sibling processes to register,
        /// then returns isMaster=true with all aggregated paths.
        /// </summary>
        public static (bool isMaster, List<string> allPaths) RegisterAndAccumulate(IEnumerable<string> initialPaths)
        {
            var cleanedList = initialPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim('"', ' ', '\t'))
                .Where(p => File.Exists(p) || Directory.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (cleanedList.Count == 0)
            {
                return (true, new List<string>());
            }

            using var mutex = new Mutex(false, MutexName);
            bool acquired = false;
            try
            {
                acquired = mutex.WaitOne(3000);

                var existingSession = ReadSession();
                var now = DateTime.UtcNow;

                // If a session was created within the last 450ms, this is a secondary process in the same multi-select invocation.
                if (existingSession != null && (now - existingSession.CreatedTime).TotalMilliseconds < 450)
                {
                    foreach (var path in cleanedList)
                    {
                        if (!existingSession.Paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                        {
                            existingSession.Paths.Add(path);
                        }
                    }
                    SaveSession(existingSession);
                    return (false, new List<string>());
                }

                // Otherwise, this is the primary (master) process.
                var newSession = new DeleteSessionData
                {
                    CreatedTime = now,
                    Paths = new List<string>(cleanedList)
                };
                SaveSession(newSession);
            }
            finally
            {
                if (acquired)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                    acquired = false;
                }
            }

            // Primary process waits briefly (350ms) to allow Explorer sibling processes to deliver their paths.
            Thread.Sleep(350);

            try
            {
                acquired = mutex.WaitOne(3000);
                var finalSession = ReadSession();
                var finalPaths = finalSession != null && finalSession.Paths.Count > 0
                    ? finalSession.Paths
                    : cleanedList;

                // Delete the session file so subsequent independent deletes start fresh
                try
                {
                    if (File.Exists(SessionFilePath))
                    {
                        File.Delete(SessionFilePath);
                    }
                }
                catch { }

                return (true, finalPaths);
            }
            finally
            {
                if (acquired)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }

        private static DeleteSessionData? ReadSession()
        {
            try
            {
                if (File.Exists(SessionFilePath))
                {
                    string json = File.ReadAllText(SessionFilePath);
                    return JsonSerializer.Deserialize<DeleteSessionData>(json);
                }
            }
            catch { }
            return null;
        }

        private static void SaveSession(DeleteSessionData data)
        {
            try
            {
                if (!Directory.Exists(AppDataDir))
                {
                    Directory.CreateDirectory(AppDataDir);
                }

                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SessionFilePath, json);
            }
            catch { }
        }
    }
}
