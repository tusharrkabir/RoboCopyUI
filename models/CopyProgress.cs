using System;
using System.Collections.Generic;

namespace RoboCopyUI
{
    /// <summary>
    /// One skipped file, with why it was skipped.
    /// </summary>
    public class SkippedFileEntry
    {
        public string FileName { get; }
        public string OriginalPath { get; }
        public long SizeBytes { get; }
        public string Reason { get; }

        public SkippedFileEntry(string fileName, string originalPath, long sizeBytes, string reason)
        {
            FileName = fileName;
            OriginalPath = originalPath;
            SizeBytes = sizeBytes;
            Reason = reason;
        }

        public string SizeDisplay => CopyProgress.FormatBytes(SizeBytes);
    }

    /// <summary>
    /// Snapshot of where a copy operation currently stands. TelemetryService
    /// raises ProgressChanged with one of these every ~250ms tick; MainWindow
    /// reads from it instead of hardcoding text.
    ///
    /// This is a plain data holder, not the CopyState — a copy can be at 80%
    /// progress AND be in the Paused state at the same time, so the two are
    /// deliberately separate rather than folded together.
    /// </summary>
    public class CopyProgress
    {
        public long BytesCopied { get; set; }
        public long BytesTotal { get; set; }

        // Kept for compatibility with ScanService/other callers that still set
        // these — no longer driven by TelemetryService (progress is byte-only
        // now), and no longer displayed in the UI.
        public int FilesCopied { get; set; }
        public int FilesTotal { get; set; }

        public double SpeedBytesPerSecond { get; set; }        // smoothed "current speed" from TelemetryService
        public double AverageSpeedBytesPerSecond { get; set; }  // total bytes copied / total elapsed time
        public double PeakSpeedBytesPerSecond { get; set; }     // highest smoothed speed seen so far this job

        public TimeSpan Elapsed { get; set; }
        public TimeSpan? Remaining { get; set; } // null until EtaEstimator has a reliable speed reading

        // No longer updated by TelemetryService (per-file tracking was
        // removed) — kept only so it doesn't break any other code that reads
        // it. UI no longer displays this.
        public string CurrentFileName { get; set; } = string.Empty;

        public List<SkippedFileEntry> SkippedFiles { get; } = new List<SkippedFileEntry>();

        // ---- Derived display values -----------------------------------------
        // MainWindow should call these instead of formatting numbers itself, so
        // there's exactly one place that decides what "GB" vs "MB" looks like.

        // Never round an unfinished job up to 100%. A remaining 6.5 MB in a
        // 12.5 GB job is only ~0.05%, but rounding made the old UI claim 100%
        // while it still displayed remaining bytes.
        public int PercentComplete => BytesTotal <= 0 ? 0 :
            BytesCopied >= BytesTotal ? 100 :
            Math.Min(99, (int)(BytesCopied * 100.0 / BytesTotal));

        public string CopiedDisplay => FormatBytes(BytesCopied);
        public string TotalDisplay => FormatBytes(BytesTotal);
        public string RemainingBytesDisplay => FormatBytes(Math.Max(0, BytesTotal - BytesCopied));

        public string SpeedDisplay => $"{FormatBytes((long)SpeedBytesPerSecond)}/s";
        public string AverageSpeedDisplay => $"{FormatBytes((long)AverageSpeedBytesPerSecond)}/s";
        public string PeakSpeedDisplay => $"{FormatBytes((long)PeakSpeedBytesPerSecond)}/s";

        public string RemainingTimeDisplay =>
            Remaining.HasValue ? FormatTime(Remaining.Value) : "--:--";

        public string ElapsedTimeDisplay => FormatTime(Elapsed);

        public long SkippedBytesTotal
        {
            get
            {
                long total = 0;
                foreach (var file in SkippedFiles)
                    total += file.SizeBytes;
                return total;
            }
        }

        // Size-only summary for the "Completed" card — no file count, per
        // the simplified bytes-only progress model.
        public string SkippedBytesDisplay =>
            SkippedBytesTotal == 0 ? "None" : FormatBytes(SkippedBytesTotal);

        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unitIndex = 0;

            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            return $"{size:0.#} {units[unitIndex]}";
        }

        public static string FormatTime(TimeSpan span)
        {
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes:00}:{span.Seconds:00}";
        }
    }
}
