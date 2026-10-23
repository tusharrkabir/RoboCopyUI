using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RoboCopyUI
{
    public class RoboDeleteRunner : IDisposable
    {
        private Process? _process;
        private Stopwatch? _stopwatch;
        private readonly CopyProgress _progress = new CopyProgress();
        public CopyProgress Progress => _progress;

        private volatile bool _cancelRequested;
        private volatile bool _isPaused;
        private bool _completedFired;

        private long _deletedBytes;
        private int _deletedFiles;
        private string? _currentEmptyDir;

        public event EventHandler<CopyProgress>? ProgressChanged;
        public event EventHandler<CopyState>? StatusChanged;
        public event EventHandler<string>? Error;
        public event EventHandler<string>? FileChanged;
        public event EventHandler<SkippedFileEntry>? SkippedFileFound;
        public event EventHandler? Completed;
        public event EventHandler? Cancelled;
        public event EventHandler<int>? ThreadCountChanged;

        public void Run(List<string> targetPaths, RobocopyOptions options)
        {
            _cancelRequested = false;
            _isPaused = false;
            _completedFired = false;
            _deletedBytes = 0;
            _deletedFiles = 0;

            StatusChanged?.Invoke(this, CopyState.Scanning);

            var validPaths = targetPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim('"', ' ', '\t'))
                .Where(p => File.Exists(p) || Directory.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (validPaths.Count == 0)
            {
                _progress.BytesTotal = 1;
                _progress.BytesCopied = 1;
                FireCompleted();
                return;
            }

            var dirsToDelete = validPaths.Where(Directory.Exists).ToList();
            var filesToDelete = validPaths.Where(File.Exists).ToList();

            long preScanBytes = 0;
            int preScanFiles = 0;
            long maxFileBytes = 0;
            int smallFiles = 0;
            bool isMassiveFolder = false;
            var scanStopwatch = Stopwatch.StartNew();

            // 1. Fast pre-scan files
            foreach (var f in filesToDelete)
            {
                if (_cancelRequested) { FireCancelled(); return; }
                try
                {
                    var fi = new FileInfo(f);
                    long len = fi.Length;
                    preScanBytes += len;
                    preScanFiles++;
                    if (len > maxFileBytes) maxFileBytes = len;
                    if (len <= 8 * 1024 * 1024) smallFiles++;
                }
                catch { }
            }

            // 2. Fast pre-scan directories (capped at 250ms so we don't stall deletion of massive folders)
            foreach (var d in dirsToDelete)
            {
                if (_cancelRequested) { FireCancelled(); return; }
                if (scanStopwatch.ElapsedMilliseconds > 250)
                {
                    isMassiveFolder = true;
                    break;
                }

                try
                {
                    var di = new DirectoryInfo(d);
                    foreach (var fi in di.EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        if (_cancelRequested) { FireCancelled(); return; }
                        if (scanStopwatch.ElapsedMilliseconds > 250)
                        {
                            isMassiveFolder = true;
                            break;
                        }
                        try
                        {
                            long len = fi.Length;
                            preScanBytes += len;
                            preScanFiles++;
                            if (len > maxFileBytes) maxFileBytes = len;
                            if (len <= 8 * 1024 * 1024) smallFiles++;
                        }
                        catch { }
                    }
                }
                catch { }
            }

            _progress.BytesTotal = Math.Max(1, preScanBytes);
            _progress.FilesTotal = Math.Max(1, preScanFiles);
            _progress.BytesCopied = 0;

            int threadCount;
            if (options.AutoSelectThreadCount)
            {
                threadCount = DriveTypeDetector.GetOptimizedDeleteThreads(
                    validPaths, preScanBytes, preScanFiles, maxFileBytes, smallFiles, isMassiveFolder);
                options.ThreadCount = threadCount;
            }
            else
            {
                threadCount = options.ThreadCount > 0 ? options.ThreadCount : 64;
            }
            ThreadCountChanged?.Invoke(this, threadCount);

            _stopwatch = Stopwatch.StartNew();
            StatusChanged?.Invoke(this, CopyState.Copying);

            // Timer to periodically update speed and UI telemetry
            using var progressTimer = new System.Timers.Timer(250);
            progressTimer.Elapsed += (s, e) => UpdateTelemetry();
            progressTimer.AutoReset = true;
            progressTimer.Start();

            try
            {
                // 3. Delete individual files using high-speed parallel deletion
                if (filesToDelete.Count > 0 && !_cancelRequested)
                {
                    DeleteFilesParallel(filesToDelete, threadCount);
                }

                // 4. Delete directories using high-speed multi-threaded Robocopy /MIR into an empty directory
                if (dirsToDelete.Count > 0 && !_cancelRequested)
                {
                    _currentEmptyDir = Path.Combine(Path.GetTempPath(), "RoboCopy_DelEmpty_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(_currentEmptyDir);

                    foreach (var dir in dirsToDelete)
                    {
                        if (_cancelRequested) break;
                        while (_isPaused && !_cancelRequested) Thread.Sleep(100);
                        if (_cancelRequested) break;

                        DeleteDirectoryWithRobocopy(_currentEmptyDir, dir, threadCount);
                    }
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, $"Deletion error: {ex.Message}");
            }
            finally
            {
                progressTimer.Stop();
                CleanupEmptyDir();
            }

            if (_cancelRequested)
            {
                FireCancelled();
            }
            else
            {
                _progress.BytesCopied = _progress.BytesTotal;
                UpdateTelemetry();
                FireCompleted();
            }
        }

        /// <summary>
        /// High-speed parallel file deletion. Groups files by parent directory:
        /// - Groups of 4+ files in the same directory use robocopy /MIR bulk delete
        /// - Smaller groups use Parallel.ForEach for concurrent File.Delete
        /// </summary>
        private void DeleteFilesParallel(List<string> filesToDelete, int threadCount)
        {
            // Group files by parent directory — if a directory has many files,
            // use robocopy /MIR with file masks for maximum throughput.
            var groupedByDir = filesToDelete
                .GroupBy(f => Path.GetDirectoryName(f) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var group in groupedByDir)
            {
                if (_cancelRequested) break;
                while (_isPaused && !_cancelRequested) Thread.Sleep(100);
                if (_cancelRequested) break;

                var files = group.ToList();

                if (files.Count >= 4 && files.Count <= 100 && !string.IsNullOrEmpty(group.Key))
                {
                    // Use robocopy bulk delete for groups of 4-100 files in the same directory
                    DeleteFileGroupWithRobocopy(group.Key, files, threadCount);
                }
                else
                {
                    // For small groups, use direct parallel deletion
                    Parallel.ForEach(files, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Min(threadCount, files.Count),
                        CancellationToken = CancellationToken.None
                    }, file =>
                    {
                        if (_cancelRequested) return;
                        while (_isPaused && !_cancelRequested) Thread.Sleep(100);
                        if (_cancelRequested) return;

                        try
                        {
                            long size = 0;
                            try { size = new FileInfo(file).Length; } catch { }

                            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                            File.Delete(file);

                            Interlocked.Add(ref _deletedBytes, size);
                            Interlocked.Increment(ref _deletedFiles);
                            _progress.BytesCopied = Math.Min(_progress.BytesTotal, Interlocked.Read(ref _deletedBytes));
                            _progress.CurrentFileName = Path.GetFileName(file);
                            FileChanged?.Invoke(this, file);
                        }
                        catch (Exception)
                        {
                            var entry = new SkippedFileEntry(Path.GetFileName(file), file, 0, "File In Use");
                            lock (_progress.SkippedFiles)
                            {
                                _progress.SkippedFiles.Add(entry);
                            }
                            SkippedFileFound?.Invoke(this, entry);
                        }
                    });
                }
            }
        }

        /// <summary>
        /// Uses robocopy /MIR to bulk-delete a group of files from the same parent directory.
        /// Creates a temporary empty directory and mirrors it to the parent with specific file masks,
        /// causing robocopy to purge those files at native speed with multi-threaded I/O.
        /// </summary>
        private void DeleteFileGroupWithRobocopy(string parentDir, List<string> files, int threadCount)
        {
            string emptyDir = Path.Combine(Path.GetTempPath(), "RoboCopy_DelFiles_" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(emptyDir);

                // Build the file list for robocopy — each filename as a separate argument
                var fileNames = files.Select(f => Path.GetFileName(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                string fileArgs = string.Join(" ", fileNames.Select(fn => $"\"{fn}\""));

                string emptyArg = RobocopyCommandBuilder.FormatRobocopyPath(emptyDir);
                string targetArg = RobocopyCommandBuilder.FormatRobocopyPath(parentDir);

                // Robocopy: mirror empty dir to parent dir, but ONLY for the specified files
                // /MIR: Mirror mode — since empty dir has none of these files, they get purged
                // /R:0 /W:0: No retries on locked files
                // /MT: Multi-threaded
                // /XJ: Exclude junctions
                // /LEV:1: Only process the immediate directory (don't recurse into subdirs)
                string args = $"{emptyArg} {targetArg} {fileArgs} /MIR /R:0 /W:0 /MT:{threadCount} /XJ /BYTES /FP /NJH /NJS /NDL /NC /NP /FFT /IPG:0 /LEV:1";

                var startInfo = new ProcessStartInfo
                {
                    FileName = "robocopy.exe",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                var tcsExited = new TaskCompletionSource<bool>();
                proc.Exited += (s, e) => tcsExited.TrySetResult(true);

                proc.Start();
                try { proc.PriorityClass = ProcessPriorityClass.High; } catch { }

                var readerTask = Task.Run(async () =>
                {
                    char[] buffer = new char[2048];
                    var sb = new System.Text.StringBuilder();
                    try
                    {
                        var reader = proc.StandardOutput;
                        int read;
                        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            for (int i = 0; i < read; i++)
                            {
                                char c = buffer[i];
                                if (c == '\r' || c == '\n')
                                {
                                    if (sb.Length > 0)
                                    {
                                        ProcessLine(sb.ToString());
                                        sb.Clear();
                                    }
                                }
                                else
                                {
                                    sb.Append(c);
                                }
                            }
                        }
                        if (sb.Length > 0) ProcessLine(sb.ToString());
                    }
                    catch { }
                });

                while (!tcsExited.Task.IsCompleted)
                {
                    if (_cancelRequested)
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        break;
                    }
                    tcsExited.Task.Wait(150);
                }

                Task.WaitAny(readerTask, Task.Delay(300));
            }
            catch (Exception)
            {
                // Fallback: if robocopy approach fails, delete files directly in parallel
                Parallel.ForEach(files, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(threadCount, files.Count)
                }, file =>
                {
                    if (_cancelRequested) return;
                    try
                    {
                        long size = 0;
                        try { size = new FileInfo(file).Length; } catch { }
                        try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                        File.Delete(file);

                        Interlocked.Add(ref _deletedBytes, size);
                        Interlocked.Increment(ref _deletedFiles);
                        _progress.BytesCopied = Math.Min(_progress.BytesTotal, Interlocked.Read(ref _deletedBytes));
                        _progress.CurrentFileName = Path.GetFileName(file);
                        FileChanged?.Invoke(this, file);
                    }
                    catch
                    {
                        var entry = new SkippedFileEntry(Path.GetFileName(file), file, 0, "File In Use");
                        lock (_progress.SkippedFiles) { _progress.SkippedFiles.Add(entry); }
                        SkippedFileFound?.Invoke(this, entry);
                    }
                });
            }
            finally
            {
                try { if (Directory.Exists(emptyDir)) Directory.Delete(emptyDir, false); } catch { }
            }
        }

        private void DeleteDirectoryWithRobocopy(string emptyDir, string targetDir, int threadCount)
        {
            string emptyArg = RobocopyCommandBuilder.FormatRobocopyPath(emptyDir);
            string targetArg = RobocopyCommandBuilder.FormatRobocopyPath(targetDir);

            // Robocopy flags for ultra-fast deletion:
            // /MIR: Mirror empty directory to target (deletes all contents inside target)
            // /R:0 /W:0: No retries/wait on locked files
            // /MT: Multi-threaded directory and file unlinking (e.g. 64 threads on SSD)
            // /XJ: Exclude junctions
            // /BYTES /FP /NJH /NJS /NDL /NC /NP: Clean output stream for live progress tracking
            string args = $"{emptyArg} {targetArg} *.* /MIR /R:0 /W:0 /MT:{threadCount} /XJ /BYTES /FP /NJH /NJS /NDL /NC /NP /FFT /IPG:0";

            var startInfo = new ProcessStartInfo
            {
                FileName = "robocopy.exe",
                Arguments = args,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                var tcsExited = new TaskCompletionSource<bool>();
                _process.Exited += (s, e) => tcsExited.TrySetResult(true);

                _process.Start();
                try { _process.PriorityClass = ProcessPriorityClass.High; } catch { }

                var readerTask = Task.Run(async () =>
                {
                    char[] buffer = new char[16384];
                    var sb = new System.Text.StringBuilder();
                    try
                    {
                        var reader = _process.StandardOutput;
                        int read;
                        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            for (int i = 0; i < read; i++)
                            {
                                char c = buffer[i];
                                if (c == '\r' || c == '\n')
                                {
                                    if (sb.Length > 0)
                                    {
                                        ProcessLine(sb.ToString());
                                        sb.Clear();
                                    }
                                }
                                else
                                {
                                    sb.Append(c);
                                }
                            }
                        }
                        if (sb.Length > 0) ProcessLine(sb.ToString());
                    }
                    catch { }
                });

                while (!tcsExited.Task.IsCompleted)
                {
                    if (_cancelRequested)
                    {
                        try { NativeMethods.ResumeProcess(_process.Handle); } catch { }
                        try { _process.Kill(entireProcessTree: true); } catch { }
                        break;
                    }
                    tcsExited.Task.Wait(150);
                }

                Task.WaitAny(readerTask, Task.Delay(300));

                // Once robocopy purges all contents, delete the now-empty root target folder itself
                if (!_cancelRequested)
                {
                    try
                    {
                        if (Directory.Exists(targetDir))
                        {
                            File.SetAttributes(targetDir, FileAttributes.Normal);
                            Directory.Delete(targetDir, false);
                        }
                    }
                    catch
                    {
                        // Fallback in case a deeply nested read-only subfolder remained
                        try { Directory.Delete(targetDir, recursive: true); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, $"Robocopy delete failed: {ex.Message}");
            }
            finally
            {
                try { _process?.Dispose(); } catch { }
                _process = null;
            }
        }

        private void ProcessLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            string trimmed = line.Trim();
            // Robocopy /NC /NDL /FP produces: "<size>\t<path>"
            var tokens = trimmed.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 2)
            {
                string path = tokens[tokens.Length - 1].Trim();
                if (long.TryParse(tokens[0].Trim(), out long size) && size >= 0)
                {
                    Interlocked.Add(ref _deletedBytes, size);
                    Interlocked.Increment(ref _deletedFiles);

                    long currentDeleted = Interlocked.Read(ref _deletedBytes);

                    // Dynamically increase BytesTotal if we encounter more than the initial pre-scan discovered
                    if (currentDeleted > _progress.BytesTotal)
                    {
                        _progress.BytesTotal = currentDeleted + (currentDeleted / 10);
                    }

                    _progress.BytesCopied = currentDeleted;
                    _progress.CurrentFileName = Path.GetFileName(path);
                    FileChanged?.Invoke(this, path);
                }
            }
        }

        private void UpdateTelemetry()
        {
            if (_stopwatch == null) return;

            double elapsedSec = _stopwatch.Elapsed.TotalSeconds;
            if (elapsedSec > 0.1)
            {
                long currentDeleted = Interlocked.Read(ref _deletedBytes);
                double speed = currentDeleted / elapsedSec;
                _progress.SpeedBytesPerSecond = speed;
                _progress.AverageSpeedBytesPerSecond = speed;
                if (speed > _progress.PeakSpeedBytesPerSecond)
                    _progress.PeakSpeedBytesPerSecond = speed;

                long remainingBytes = Math.Max(0, _progress.BytesTotal - currentDeleted);
                if (speed > 1000)
                {
                    _progress.Remaining = TimeSpan.FromSeconds(remainingBytes / speed);
                }
            }

            _progress.Elapsed = _stopwatch.Elapsed;
            ProgressChanged?.Invoke(this, _progress);
        }

        private void CleanupEmptyDir()
        {
            try
            {
                if (!string.IsNullOrEmpty(_currentEmptyDir) && Directory.Exists(_currentEmptyDir))
                {
                    Directory.Delete(_currentEmptyDir, false);
                }
            }
            catch { }
            _currentEmptyDir = null;
        }

        public void Pause()
        {
            _isPaused = true;
            if (_process != null && !_process.HasExited)
            {
                try { NativeMethods.SuspendProcess(_process.Handle); } catch { }
            }
            _stopwatch?.Stop();
            StatusChanged?.Invoke(this, CopyState.Paused);
        }

        public void Resume()
        {
            _isPaused = false;
            if (_process != null && !_process.HasExited)
            {
                try { NativeMethods.ResumeProcess(_process.Handle); } catch { }
            }
            if (_stopwatch != null && !_stopwatch.IsRunning) _stopwatch.Start();
            StatusChanged?.Invoke(this, CopyState.Copying);
        }

        public void Cancel()
        {
            _cancelRequested = true;
            if (_process != null && !_process.HasExited)
            {
                try { NativeMethods.ResumeProcess(_process.Handle); } catch { }
                try { _process.Kill(entireProcessTree: true); } catch { }
            }
            CleanupEmptyDir();
            FireCancelled();
        }

        private void FireCompleted()
        {
            if (_completedFired) return;
            _completedFired = true;
            _stopwatch?.Stop();
            StatusChanged?.Invoke(this, CopyState.Completed);
            Completed?.Invoke(this, EventArgs.Empty);
        }

        private void FireCancelled()
        {
            if (_completedFired) return;
            _completedFired = true;
            _stopwatch?.Stop();
            StatusChanged?.Invoke(this, CopyState.Cancelled);
            Cancelled?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Cancel();
            try { _process?.Dispose(); } catch { }
        }
    }
}
