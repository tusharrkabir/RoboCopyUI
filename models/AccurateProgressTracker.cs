using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace RoboCopyUI
{
    // The progress total comes from the pre-scan. The copied amount comes only
    // from Robocopy's own file records; it never uses the size of the whole
    // destination directory (which also contains old and unrelated data).
    public sealed class AccurateProgressTracker
    {
        private readonly Dictionary<string, long> _filesToCopy;
        private readonly long _totalBytesToCopy;
        private readonly string _sourceRoot;
        private readonly string _destinationRoot;
        private readonly HashSet<string> _completedFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new();

        private long _completedBytes;
        private string? _activeFileKey;
        private long _activeFileBytes;

        private static readonly Regex PercentRegex = new(@"(?<!\d)(?<percent>\d{1,3}(?:\.\d+)?)%", RegexOptions.Compiled);
        private static readonly Regex TrailingPercentRegex = new(@"\s*\d{1,3}(?:\.\d+)?%\s*$", RegexOptions.Compiled);

        public event EventHandler<AccurateProgressEventArgs>? ProgressUpdated;

        public AccurateProgressTracker(PreScanResult preScanResult, string sourceRoot, string destinationRoot)
        {
            _sourceRoot = Path.GetFullPath(sourceRoot).TrimEnd('\\') + "\\";
            _destinationRoot = Path.GetFullPath(destinationRoot).TrimEnd('\\') + "\\";
            _filesToCopy = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in preScanResult.FilesToCopy ?? new Dictionary<string, long>())
            {
                var key = NormalizePathKey(item.Key);
                _filesToCopy[key] = item.Value;
            }

            _totalBytesToCopy = preScanResult.TotalBytesToCopy;
        }

        public void ParseRobocopyLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            // Parse the file record without its trailing carriage-return percentage
            var fileRecord = TrailingPercentRegex.Replace(line, string.Empty);
            var parsed = RobocopyLineParser.ParseFileLine(fileRecord);
            string? key = null;
            if (parsed.HasValue && !parsed.Value.isDirectory &&
                Array.IndexOf(RobocopyLineParser.SkipTags, parsed.Value.tag) < 0)
            {
                key = ResolveKey(parsed.Value.path);
                if (key != null)
                {
                    lock (_sync)
                    {
                        _activeFileKey = key;
                        _activeFileBytes = 0;
                    }
                }
            }

            var percent = PercentRegex.Match(line);
            if (percent.Success && double.TryParse(percent.Groups["percent"].Value,
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                UpdateActiveFile(key, Math.Clamp(value, 0, 100));
            }
            else if (key != null)
            {
                // In /NP mode or when percentages are omitted, a non-skipped file line represents file completion.
                UpdateActiveFile(key, 100);
            }
        }

        private void UpdateActiveFile(string? keyFromLine, double percent)
        {
            AccurateProgressEventArgs? update = null;
            lock (_sync)
            {
                var key = keyFromLine ?? _activeFileKey;
                if (key == null || !_filesToCopy.TryGetValue(key, out var size) || _completedFiles.Contains(key))
                    return;

                _activeFileKey = key;
                _activeFileBytes = percent >= 100 ? 0 : (long)Math.Floor(size * percent / 100d);
                if (percent >= 100)
                {
                    _completedFiles.Add(key);
                    _completedBytes += size;
                    _activeFileKey = null;
                    _activeFileBytes = 0;
                }

                update = CreateEventArgs();
            }
            ProgressUpdated?.Invoke(this, update);
        }

        public void ForceComplete()
        {
            AccurateProgressEventArgs update;
            lock (_sync)
            {
                _completedBytes = _totalBytesToCopy;
                _activeFileKey = null;
                _activeFileBytes = 0;
                update = CreateEventArgs();
            }
            ProgressUpdated?.Invoke(this, update);
        }

        public AccurateProgressEventArgs GetSnapshot()
        {
            lock (_sync) return CreateEventArgs();
        }

        private AccurateProgressEventArgs CreateEventArgs()
        {
            var overall = Math.Min(_totalBytesToCopy, _completedBytes + _activeFileBytes);
            return new AccurateProgressEventArgs
            {
                CompletedBytes = _completedBytes,
                CurrentFileBytes = _activeFileBytes,
                OverallBytes = overall,
                TotalBytes = _totalBytesToCopy,
                OverallPercent = _totalBytesToCopy == 0 ? 0 : overall * 100d / _totalBytesToCopy,
                CurrentFileName = _activeFileKey ?? string.Empty,
                CurrentFilePercent = _activeFileKey == null || !_filesToCopy.TryGetValue(_activeFileKey, out var size) || size == 0
                    ? 0 : _activeFileBytes * 100d / size
            };
        }

        private string? ResolveKey(string reportedPath)
        {
            if (string.IsNullOrWhiteSpace(reportedPath)) return null;
            var path = reportedPath.Trim().Trim('"');

            if (Path.IsPathRooted(path))
            {
                var full = Path.GetFullPath(path);
                if (_filesToCopy.ContainsKey(full))
                    return full;

                if (full.StartsWith(_sourceRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var rel = NormalizeRelative(full.Substring(_sourceRoot.Length));
                    if (_filesToCopy.ContainsKey(rel)) return rel;
                }
                if (full.StartsWith(_destinationRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var rel = NormalizeRelative(full.Substring(_destinationRoot.Length));
                    if (_filesToCopy.ContainsKey(rel)) return rel;
                }
            }
            else
            {
                var rel = NormalizeRelative(path);
                if (_filesToCopy.ContainsKey(rel)) return rel;
            }

            return null;
        }

        private static string NormalizePathKey(string path)
        {
            var trimmed = path.Trim().Trim('"');
            return Path.IsPathRooted(trimmed) ? Path.GetFullPath(trimmed) : NormalizeRelative(trimmed);
        }

        private static string NormalizeRelative(string path) => path.Trim().TrimStart('\\', '/').Replace('/', '\\');
    }

    public class AccurateProgressEventArgs : EventArgs
    {
        public long CompletedBytes { get; set; }
        public long CurrentFileBytes { get; set; }
        public long OverallBytes { get; set; }
        public long TotalBytes { get; set; }
        public double OverallPercent { get; set; }
        public string CurrentFileName { get; set; } = string.Empty;
        public double CurrentFilePercent { get; set; }
    }
}
