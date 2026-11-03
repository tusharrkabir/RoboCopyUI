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
        public bool IsMove { get; set; }
        public bool FullPreservation { get; set; }
        public int RetryCount { get; set; } = 3;
        public int WaitSeconds { get; set; } = 5;
        public int ThreadCount { get; set; } = 8;
        public bool AutoSelectThreadCount { get; set; } = true;
        public bool UnbufferedIO { get; set; } = false;
        public bool PreserveTimestamps { get; set; } = true;
        public RobocopyOptions Snapshot() => (RobocopyOptions)MemberwiseClone();
    }

    public class RobocopyEngine : IDisposable
    {
        private Process? _process;
        private Stopwatch? _stopwatch;

        private readonly CopyProgress _progress = new CopyProgress();
        public CopyProgress Progress => _progress;

        private volatile bool _cancelRequested;
        private bool _completedFired;
        private bool _failed;
        private int _started;
        public List<string> Results { get; } = new();

        private PendingFileTable? _pending;
        private InFlightSet? _inFlight;
        private StdoutParser? _stdoutParser;
        private TelemetryService? _telemetry;

        private AccurateProgressTracker? _accurateProgress;
        private PreScanResult _preScanResult = new PreScanResult();

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
            // The old mirror-delete runner has no per-item confirmation or safe scope checks.
            // Keep this compatibility API closed until the Shell deletion path passes acceptance.
            Error?.Invoke(this, L.T("旧版高速删除入口已停用；永久删除须通过新的确认与范围验收。"));
            StatusChanged?.Invoke(this, CopyState.Error);
            return;
        }

        public void Start(List<string> sourcePaths, string destination, RobocopyOptions? options = null)
        {
            if(System.Threading.Interlocked.Exchange(ref _started,1)!=0)
                throw new InvalidOperationException(L.T("每个传输引擎只能运行一个任务；重试必须新建任务快照。"));
            try
            {
                if(_cancelRequested)throw new OperationCanceledException();
                var frozenPaths = sourcePaths.Select(TransferSafety.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (frozenPaths.Any(p => !File.Exists(p) && !Directory.Exists(p)))
                    throw new IOException(L.T("部分源已不存在；没有执行任务。"));
                RunStart(frozenPaths, TransferSafety.Normalize(destination), (options ?? new RobocopyOptions()).Snapshot());
            }
            catch (OperationCanceledException)
            {
                _cancelRequested=true;
                OnAllBatchesCompleted();
            }
            catch (Exception ex)
            {
                _failed = true;
                Error?.Invoke(this, $"Start failed: {ex.Message}");
                OnAllBatchesCompleted();
            }
        }

        private void RunStart(List<string> sourcePaths, string destination, RobocopyOptions options)
        {
            _completedFired = false;
            _failed = false;
            Results.Clear();
            StatusChanged?.Invoke(this, CopyState.Scanning);

            var initialBatches = RobocopyBatchItem.CreateBatches(sourcePaths, destination);
            TransferSafety.Validate(initialBatches,()=>_cancelRequested);
            if(options.FullPreservation)
            {
                var volume=VolumeCapabilities.Read(destination);
                if(!volume.PreservesAcl)throw new IOException(L.F("目标文件系统 {0} 不支持 Windows 权限，无法完整保留。源未清理；可为本次任务选择普通复制。", volume.FileSystem));
            }
            // Freeze before collision UI, scans or child processes touch the destination.
            var manifests = initialBatches.ToDictionary(b => b, b => TransferSafety.Freeze(b, () => _cancelRequested,options.IsMove));
            foreach (var warning in manifests.Values.SelectMany(m => m.Warnings))
            {
                Results.Add(warning);
                // Copying can proceed, but the task must not be presented as fully complete.
                _failed = true;
                Error?.Invoke(this, warning);
            }
            if (initialBatches.Count == 0)
            {
                StatusChanged?.Invoke(this, CopyState.Completed);
                Completed?.Invoke(this, EventArgs.Empty);
                return;
            }

            var excludeFromCopy = new List<CollisionEntry>();
            bool hasSkipped = false;
            bool forceOverwriteSame = false;

            // 1. Single consolidated collision scan across ALL batches up front
            var allCollisions = new List<CollisionEntry>();
            foreach (var batch in initialBatches)
            {
                if (_cancelRequested) throw new OperationCanceledException();
                var collisions = CollisionScanner.FindCollisions(batch);
                allCollisions.AddRange(collisions);
            }

            if (allCollisions.Count > 0 && !_cancelRequested)
            {
                var tcs = new TaskCompletionSource<CollisionChoice>();
                _pendingCollisionChoice = tcs;
                if(CollisionsDetected==null)throw new IOException(L.T("存在冲突，需要明确选择处理方式；未执行传输。"));
                CollisionsDetected?.Invoke(this, allCollisions);

                var choice = tcs.Task.Result;
                if (_cancelRequested) throw new OperationCanceledException();

                if (choice == CollisionChoice.Skip)
                {
                    hasSkipped = true;
                    excludeFromCopy.AddRange(allCollisions);
                }
                else if (choice == CollisionChoice.Rename)
                {
                    foreach (var entry in allCollisions)
                    {
                        if (_cancelRequested) break;
                        var target = CollisionRenamer.RenameAndCopy(entry, options, () => _cancelRequested, Results.Add);
                        foreach(var manifest in manifests.Values)manifest.RemapTarget(entry.SourceFullPath,target);
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

                    if (batch.IsDirectoryCopy && excludeFromCopy.Any(e =>
                        string.Equals(e.SourceFullPath, batch.SourceDir, StringComparison.OrdinalIgnoreCase)))
                        continue;
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
                    if (batch.IsDirectoryCopy && excludeFromCopy.Any(e =>
                        string.Equals(e.SourceFullPath, batch.SourceDir, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var startInfo = RobocopyCommandBuilder.CreateStartInfo(batch, options, false, excludeFromCopy, forceOverwriteSame);

                    try
                    {
                        var outputLog = new System.Text.StringBuilder();
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
                                                if (outputLog.Length < 131072) outputLog.AppendLine(line);
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

                        _process.WaitForExit();
                        readerTask.GetAwaiter().GetResult();

                        _telemetry?.OnBatchCompleted();

                        Results.Add($"{batch.SourceDir} → {batch.DestDir}; exit={_process.ExitCode}; {string.Join(" ", startInfo.ArgumentList)}\n{outputLog}");
                        if (_process.ExitCode >= 8)
                        {
                            _failed = true;
                            Error?.Invoke(this, L.F("复制失败（退出码 {0}），源保留。已复制内容可能仍在目标：{1}\n{2}", _process.ExitCode, batch.DestDir, outputLog));
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _failed = true;
                        Error?.Invoke(this, $"Robocopy error: {ex.Message}");
                        break;
                    }
                }
                // Never clean earlier batches if a later batch failed/cancelled.
                if (options.IsMove && !_cancelRequested && !_failed)
                {
                    if (hasSkipped)
                    {
                        _failed = true;
                        Error?.Invoke(this, L.T("有跳过的冲突项目；已复制内容保留在目标，本次源全部保留供核对。"));
                    }
                    else foreach (var manifest in manifests.Values)
                    {
                        var problems = manifest.CleanVerifiedSources(options.FullPreservation, () => _cancelRequested);
                        if (problems.Count > 0)
                        {
                            _failed = true;
                            Error?.Invoke(this, string.Join(Environment.NewLine, problems));
                            break;
                        }
                    }
                }
            }
            finally
            {
            }

            OnAllBatchesCompleted();
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

            if (_failed)
            {
                _telemetry?.Stop();
                StatusChanged?.Invoke(this, CopyState.Error);
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
            if(_completedFired)return;
            _cancelRequested = true;
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
            // The worker reports one terminal state after its child process and readers stop.
        }

        public void Dispose()
        {
            _cancelRequested = true;
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
