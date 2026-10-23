using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace RoboCopyUI
{
    // Robocopy can preallocate a destination file to its final length, so file
    // length is not progress. Read the process's actual write-transfer counter
    // instead; it advances only as Robocopy writes data (like Task Manager).
    public sealed class TelemetryService
    {
        private readonly CopyProgress _progress;
        private readonly AccurateProgressTracker _tracker;
        private readonly EtaEstimator _eta = new();
        private readonly Queue<(DateTime time, long bytes)> _samples = new();
        private readonly SpeedCalculator _speedCalculator = new();
        private System.Timers.Timer? _timer;
        private Stopwatch? _stopwatch;
        private Process? _process;
        private ulong _initialWriteBytes;
        private int _tickInProgress;
        private DateTime _lastNonZeroWriteUtc;
        private long _accumulatedBatchBytes;
        private long _currentBatchWrittenBytes;
        private long _maxBytesCopiedSoFar;

        private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan ZeroSpeedGracePeriod = TimeSpan.FromSeconds(4);
        public event EventHandler<CopyProgress>? Tick;

        public TelemetryService(CopyProgress progress, AccurateProgressTracker tracker)
        {
            _progress = progress;
            _tracker = tracker;
        }

        public void Start(Process process, Stopwatch stopwatch)
        {
            _process = process;
            _stopwatch = stopwatch;
            _initialWriteBytes = GetWriteBytes(process);
            _currentBatchWrittenBytes = 0;
            _samples.Clear();
            _speedCalculator.Reset();
            _lastNonZeroWriteUtc = DateTime.MinValue;

            if (_timer == null)
            {
                _timer = new System.Timers.Timer(250) { AutoReset = true };
                _timer.Elapsed += (s, e) => Publish();
            }
            _timer.Start();
        }

        public void Stop() => _timer?.Stop();

        public void Resume() => _timer?.Start();

        public void Publish()
        {
            if (Interlocked.Exchange(ref _tickInProgress, 1) == 1) return;
            try
            {
                var tracker = _tracker.GetSnapshot();
                var writes = GetWriteBytes(_process);
                if (writes >= _initialWriteBytes)
                {
                    _currentBatchWrittenBytes = (long)Math.Min((ulong)long.MaxValue, writes - _initialWriteBytes);
                }

                // Live progress driven by actual process writes + completed batch bytes
                var liveBytes = _accumulatedBatchBytes + _currentBatchWrittenBytes;
                var copied = Math.Min(tracker.TotalBytes, Math.Max(liveBytes, tracker.OverallBytes));
                copied = Math.Max(_maxBytesCopiedSoFar, copied);
                _maxBytesCopiedSoFar = copied;

                var now = DateTime.UtcNow;
                _samples.Enqueue((now, copied));
                while (_samples.Count > 1 && now - _samples.Peek().time > SpeedWindow)
                    _samples.Dequeue();

                var first = _samples.Peek();
                var seconds = (now - first.time).TotalSeconds;
                var rawSpeed = seconds > 0 ? Math.Max(0, copied - first.bytes) / seconds : 0;
                double speed;
                if (rawSpeed > 0)
                {
                    _lastNonZeroWriteUtc = now;
                    speed = _speedCalculator.Update(rawSpeed);
                }
                else if (_lastNonZeroWriteUtc != DateTime.MinValue &&
                         now - _lastNonZeroWriteUtc <= ZeroSpeedGracePeriod)
                {
                    speed = _speedCalculator.Current;
                }
                else
                {
                    speed = _speedCalculator.Update(0);
                }

                _progress.BytesCopied = copied;
                _progress.BytesTotal = tracker.TotalBytes;
                _progress.CurrentFileName = tracker.CurrentFileName;
                _progress.SpeedBytesPerSecond = speed;
                _progress.PeakSpeedBytesPerSecond = Math.Max(_progress.PeakSpeedBytesPerSecond, speed);
                _progress.Elapsed = _stopwatch?.Elapsed ?? TimeSpan.Zero;
                _progress.AverageSpeedBytesPerSecond = _progress.Elapsed.TotalSeconds > 0
                    ? copied / _progress.Elapsed.TotalSeconds : 0;
                _progress.Remaining = _eta.Update(Math.Max(0, tracker.TotalBytes - copied), speed);
                Tick?.Invoke(this, _progress);
            }
            finally { Interlocked.Exchange(ref _tickInProgress, 0); }
        }

        public void OnBatchCompleted(long batchSize = 0)
        {
            if (batchSize > 0)
            {
                _accumulatedBatchBytes += batchSize;
            }
            else
            {
                _accumulatedBatchBytes += _currentBatchWrittenBytes;
            }
            _currentBatchWrittenBytes = 0;
        }

        private static ulong GetWriteBytes(Process? process)
        {
            if (process == null || process.HasExited) return 0;
            try
            {
                return NativeMethods.GetProcessIoCounters(process.Handle, out var counters)
                    ? counters.WriteTransferCount : 0;
            }
            catch { return 0; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetProcessIoCounters(IntPtr hProcess, out IoCounters lpIoCounters);
        }
    }
}
