using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace RoboCopyUI
{
    public class RobocopyOptions
    {
        public string FileMask { get; set; } = "*.*";
        public bool Mirror { get; set; } = false; // IsCutMode / Mirror
        public int RetryCount { get; set; } = 0;
        public int WaitSeconds { get; set; } = 0;
        public int ThreadCount { get; set; } = 8;
        public bool AutoSelectThreadCount { get; set; } = true;
        public bool UnbufferedIO { get; set; } = false;
        public bool PreserveTimestamps { get; set; } = true;
    }

    public class RobocopyEngine : IDisposable
    {
        private Process? _process;
        private Stopwatch? _stopwatch;

        private readonly CopyProgress _progress = new CopyProgress();
        public CopyProgress Progress => _progress;

        private bool _cancelRequested;
        private bool _completedFired;

        private PendingFileTable? _pending;
        private InFlightSet? _inFlight;
        private StdoutParser? _stdoutParser;
        private TelemetryService? _telemetry;

        private AccurateProgressTracker? _accurateProgress;
        private PreScanResult _preScanResult = new PreScanResult();
        private RoboDeleteRunner? _deleteRunner;

        private TaskCompletionSource<CollisionChoice>? _pendingCollisionChoice;
        public event EventHandler<List<CollisionEntry>>? CollisionsDetected;

        public event EventHandler<CopyProgress>? ProgressChanged;
        public event EventHandler<double>? SpeedChanged;
        public event EventHandler<TimeSpan?>? ETAChanged;
        public event EventHandler<long>? RemainingChanged;
        public event EventHandler<string>? FileChanged;
        public event EventHandler<string>? FolderChanged;
        public event EventHandler<SkippedFileEntry>? SkippedFileFound;
        public event EventHandler? Completed;
        public event EventHandler? Cancelled;
        public event EventHandler<string>? Error;
        public event EventHandler<CopyState>? StatusChanged;
        public event EventHandler<int>? ThreadCountChanged;

        public void Start(string source, string destination, RobocopyOptions? options = null)
        {
            Start(new List<string> { source }, destination, options);
        }

        public void StartDelete(List<string> targetPaths, RobocopyOptions? options = null)
        {
            try
            {
                _deleteRunner = new RoboDeleteRunner();
                _deleteRunner.ProgressChanged += (s, p) =>
                {
                    _progress.BytesCopied = p.BytesCopied;
                    _progress.BytesTotal = p.BytesTotal;
                    _progress.FilesCopied = p.FilesCopied;
                    _progress.FilesTotal = p.FilesTotal;
                    _progress.SpeedBytesPerSecond = p.SpeedBytesPerSecond;
                    _progress.AverageSpeedBytesPerSecond = p.AverageSpeedBytesPerSecond;
                    _progress.PeakSpeedBytesPerSecond = p.PeakSpeedBytesPerSecond;
                    _progress.Elapsed = p.Elapsed;
                    _progress.Remaining = p.Remaining;
                    _progress.CurrentFileName = p.CurrentFileName;
                    ProgressChanged?.Invoke(this, p);
                };
                _deleteRunner.StatusChanged += (s, st) => StatusChanged?.Invoke(this, st);
                _deleteRunner.ThreadCountChanged += (s, th) => ThreadCountChanged?.Invoke(this, th);
                _deleteRunner.FileChanged += (s, f) => FileChanged?.Invoke(this, f);
                _deleteRunner.SkippedFileFound += (s, sk) =>
                {
                    _progress.SkippedFiles.Add(sk);
                    SkippedFileFound?.Invoke(this, sk);
                };
                _deleteRunner.Error += (s, err) => Error?.Invoke(this, err);
                _deleteRunner.Completed += (s, e) => Completed?.Invoke(this, e);
                _deleteRunner.Cancelled += (s, e) => Cancelled?.Invoke(this, e);

                _deleteRunner.Run(targetPaths, options ?? new RobocopyOptions());
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, $"StartDelete failed: {ex.Message}");
                StatusChanged?.Invoke(this, CopyState.Error);
            }
        }

        public void Start(List<string> sourcePaths, string destination, RobocopyOptions? options = null)
        {
            try
            {
                RunStart(sourcePaths, destination, options ?? new RobocopyOptions());
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, $"Start failed: {ex.Message}");
                StatusChanged?.Invoke(this, CopyState.Error);
            }
        }

        private void RunStart(List<string> sourcePaths, string destination, RobocopyOptions options)
        {
            _cancelRequested = false;
            _completedFired = false;
            StatusChanged?.Invoke(this, CopyState.Scanning);

            var initialBatches = RobocopyBatchItem.CreateBatches(sourcePaths, destination);
            if (initialBatches.Count == 0)
            {
                StatusChanged?.Invoke(this, CopyState.Completed);
                Completed?.Invoke(this, EventArgs.Empty);
                return;
            }

            var excludeFromCopy = new List<CollisionEntry>();
            bool forceOverwriteSame = false;

            // 1. Single consolidated collision scan across ALL batches up front
            var allCollisions = new List<CollisionEntry>();
            foreach (var batch in initialBatches)
            {
                if (_cancelRequested) return;
                var collisions = CollisionScanner.FindCollisions(batch);
                allCollisions.AddRange(collisions);
            }

            if (allCollisions.Count > 0 && !_cancelRequested)
            {
                var tcs = new TaskCompletionSource<CollisionChoice>();
                _pendingCollisionChoice = tcs;
                CollisionsDetected?.Invoke(this, allCollisions);

                var choice = tcs.Task.Result;
                if (_cancelRequested) return;

                if (choice == CollisionChoice.Skip)
                {
                    excludeFromCopy.AddRange(allCollisions);
                }
                else if (choice == CollisionChoice.Rename)
                {
                    foreach (var entry in allCollisions)
                    {
                        if (_cancelRequested) break;
                        CollisionRenamer.RenameAndCopy(entry, options);
                    }
                    excludeFromCopy.AddRange(allCollisions);
                }
                else
                {
                    // Replace ALL conflicting items across all batches
                    forceOverwriteSame = true;
                }
            }

            if (_cancelRequested)
            {
                OnAllBatchesCompleted();
                return;
            }

            // Direct batching: execute initialBatches without creating temp staging links
            List<RobocopyBatchItem> batches = initialBatches;

            try
            {
                // 2. Pre-scan total files and bytes across batches with cancellation checks
                long totalBytes = 0;
                int totalFiles = 0;
                var allFilesToCopy = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

                foreach (var batch in batches)
                {
                    if (_cancelRequested) break;
                    try
                    {
                        var scan = ScanService.Scan(batch, options, excludeFromCopy, forceOverwriteSame, () => _cancelRequested);
                        if (scan.FilesToCopy != null && scan.FilesToCopy.Count > 0)
                        {
                            foreach (var kvp in scan.FilesToCopy)
                            {
                                allFilesToCopy[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                    catch
                    {
                        if (_cancelRequested) break;
                        try
                        {
                            var preScan = PreScanService.ScanFilesToCopy(batch, options, excludeFromCopy, forceOverwriteSame, () => _cancelRequested);
                            foreach (var kvp in preScan.FilesToCopy)
                            {
                                allFilesToCopy[kvp.Key] = kvp.Value;
                            }
                        }
                        catch { }
                    }
                }

                if (_cancelRequested)
                {
                    OnAllBatchesCompleted();
                    return;
                }

                totalBytes = allFilesToCopy.Values.Sum();
                totalFiles = allFilesToCopy.Count;

                _preScanResult = new PreScanResult
                {
                    FilesToCopy = allFilesToCopy,
                    TotalBytesToCopy = totalBytes,
                    TotalFilesToCopy = totalFiles
                };

                string primarySource = sourcePaths.FirstOrDefault() ?? destination;
                _accurateProgress = new AccurateProgressTracker(_preScanResult, primarySource, destination);
                _progress.BytesTotal = Math.Max(1, _preScanResult.TotalBytesToCopy);
                _progress.FilesTotal = Math.Max(1, _preScanResult.TotalFilesToCopy);

                if (_preScanResult.TotalBytesToCopy <= 0 && batches.All(b => !b.IsDirectoryCopy))
                {
                    _accurateProgress?.ForceComplete();
                    _progress.BytesTotal = 1;
                    _progress.BytesCopied = 1;
                    StatusChanged?.Invoke(this, CopyState.Completed);
                    Completed?.Invoke(this, EventArgs.Empty);
                    return;
                }

                if (options.AutoSelectThreadCount)
                {
                    // Auto-detect storage hardware (HDD vs SSD vs NVMe) and optimize threads & buffer
                    var (optimalThreads, recommendUnbuffered) = DriveTypeDetector.GetOptimizedSettings(
                        primarySource, destination, _preScanResult.TotalBytesToCopy, _preScanResult.TotalFilesToCopy);

                    options.ThreadCount = optimalThreads;
                    options.UnbufferedIO = recommendUnbuffered;
                }

                ThreadCountChanged?.Invoke(this, options.ThreadCount);

                _stopwatch = Stopwatch.StartNew();
                _telemetry = new TelemetryService(_progress, _accurateProgress);
                _telemetry.Tick += (s, p) => ProgressChanged?.Invoke(this, p);

                _pending ??= new PendingFileTable();
                _inFlight = new InFlightSet();

                _stdoutParser = new StdoutParser(primarySource, _pending, _inFlight);
                _stdoutParser.FolderChanged += (s, path) =>
                {
                    _progress.CurrentFileName = string.Empty;
                    FolderChanged?.Invoke(this, path);
                };
                _stdoutParser.SkippedFileFound += (s, entry) =>
                {
                    _progress.SkippedFiles.Add(entry);
                    SkippedFileFound?.Invoke(this, entry);
                };
                _stdoutParser.ErrorOccurred += (s, msg) => Error?.Invoke(this, msg);

                StatusChanged?.Invoke(this, CopyState.Copying);

                // Execute batches sequentially (single batch when stagingDir is used)
                foreach (var batch in batches)
                {
                    if (_cancelRequested) break;

                    _stdoutParser?.SetSourceRoot(batch.SourceDir);

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "robocopy.exe",
                        Arguments = RobocopyCommandBuilder.BuildArguments(
                            batch, options, listOnly: false, excludeFromCopy, forceOverwriteSame),
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
                        _telemetry?.Start(_process, _stopwatch!);

                        var readerTask = Task.Run(async () =>
                        {
                            var sb = new System.Text.StringBuilder();
                            char[] buffer = new char[2048];
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
                                                string line = sb.ToString();
                                                sb.Clear();
                                                ProcessOutputLine(line);
                                            }
                                        }
                                        else
                                        {
                                            sb.Append(c);
                                        }
                                    }
                                }
                                if (sb.Length > 0)
                                {
                                    ProcessOutputLine(sb.ToString());
                                }
                            }
                            catch { }
                        });

                        while (!tcsExited.Task.IsCompleted)
                        {
                            if (_cancelRequested)
                            {
                                try { NativeMethods.ResumeProcess(_process.Handle); } catch { }
                                try { _process.Kill(); } catch { }
                                break;
                            }
                            tcsExited.Task.Wait(200);
                        }

                        Task.WaitAny(readerTask, Task.Delay(500));

                        _telemetry?.OnBatchCompleted();

                        // Perform Cut cleanup for files/folders after successful copy
                        if (options.Mirror && !_cancelRequested && _process.ExitCode < 16)
                        {
                            PerformCutCleanup(batch);
                        }
                    }
                    catch (Exception ex)
                    {
                        Error?.Invoke(this, $"Robocopy error: {ex.Message}");
                    }
                }
            }
            finally
            {
            }

            OnAllBatchesCompleted();
        }

        private void PerformCutCleanup(RobocopyBatchItem batch)
        {
            try
            {
                if (batch.IsDirectoryCopy)
                {
                    if (Directory.Exists(batch.SourceDir))
                    {
                        Directory.Delete(batch.SourceDir, recursive: true);
                    }
                }
                else
                {
                    foreach (var spec in batch.FileSpecs)
                    {
                        string file = Path.Combine(batch.SourceDir, spec);
                        if (File.Exists(file))
                        {
                            File.Delete(file);
                        }
                    }
                }
            }
            catch { }
        }

        private void PerformCutCleanup(IEnumerable<string> sourcePaths)
        {
            foreach (var path in sourcePaths)
            {
                try
                {
                    if (Directory.Exists(path))
                        Directory.Delete(path, recursive: true);
                    else if (File.Exists(path))
                        File.Delete(path);
                }
                catch { }
            }
        }

        private void ProcessOutputLine(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return;

            _accurateProgress?.ParseRobocopyLine(data);

            var parsed = RobocopyLineParser.ParseFileLine(data.Trim());
            if (parsed.HasValue && !parsed.Value.isDirectory &&
                Array.IndexOf(RobocopyLineParser.SkipTags, parsed.Value.tag) < 0)
            {
                var fileName = System.IO.Path.GetFileName(parsed.Value.path);
                _progress.CurrentFileName = fileName;
                FileChanged?.Invoke(this, parsed.Value.path);
            }

            _stdoutParser?.ParseLine(data);
        }

        public void ResolveCollisions(CollisionChoice choice)
        {
            _pendingCollisionChoice?.TrySetResult(choice);
        }

        private void OnAllBatchesCompleted()
        {
            if (_completedFired) return;
            _completedFired = true;

            if (_cancelRequested)
            {
                _telemetry?.Stop();
                StatusChanged?.Invoke(this, CopyState.Cancelled);
                Cancelled?.Invoke(this, EventArgs.Empty);
                return;
            }

            _accurateProgress?.ForceComplete();
            _telemetry?.Publish();
            _telemetry?.Stop();
            StatusChanged?.Invoke(this, CopyState.Completed);
            Completed?.Invoke(this, EventArgs.Empty);
        }

        public void Pause()
        {
            _deleteRunner?.Pause();

            if (_process == null || _process.HasExited) return;

            try
            {
                NativeMethods.SuspendProcess(_process.Handle);
            }
            catch { }

            _telemetry?.Stop();
            _stopwatch?.Stop();
            StatusChanged?.Invoke(this, CopyState.Paused);
        }

        public void Resume()
        {
            _deleteRunner?.Resume();

            if (_process == null || _process.HasExited) return;

            try
            {
                NativeMethods.ResumeProcess(_process.Handle);
            }
            catch { }

            if (_stopwatch != null && !_stopwatch.IsRunning)
                _stopwatch.Start();

            if (_process != null && !_process.HasExited)
                _telemetry?.Resume();

            StatusChanged?.Invoke(this, CopyState.Copying);
        }

        public void Cancel()
        {
            _cancelRequested = true;
            _deleteRunner?.Cancel();
            _pendingCollisionChoice?.TrySetResult(CollisionChoice.Skip); // Unblock if waiting on collision resolution

            try
            {
                var proc = _process;
                if (proc != null && !proc.HasExited)
                {
                    try { NativeMethods.ResumeProcess(proc.Handle); } catch { } // Resume if paused before killing
                    try { proc.Kill(entireProcessTree: true); } catch { }
                }
            }
            catch { }

            _telemetry?.Stop();
            StatusChanged?.Invoke(this, CopyState.Cancelled);
            Cancelled?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            _cancelRequested = true;
            _deleteRunner?.Dispose();
            _pendingCollisionChoice?.TrySetResult(CollisionChoice.Skip);
            _telemetry?.Stop();
            _stopwatch?.Stop();

            var proc = _process;
            if (proc != null)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        try { NativeMethods.ResumeProcess(proc.Handle); } catch { }
                        try { proc.Kill(entireProcessTree: true); } catch { }
                    }
                    proc.Dispose();
                }
                catch { }
            }
        }
    }

    internal static class NativeMethods
    {
        [DllImport("ntdll.dll", PreserveSig = true)]
        public static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll", PreserveSig = true)]
        public static extern int NtResumeProcess(IntPtr processHandle);

        public static void SuspendProcess(IntPtr processHandle) => NtSuspendProcess(processHandle);
        public static void ResumeProcess(IntPtr processHandle) => NtResumeProcess(processHandle);
    }
}
