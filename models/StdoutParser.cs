using System;
using System.Text.RegularExpressions;

namespace RoboCopyUI
{
    // Trimmed-down responsibility vs the old RobocopyEngine.OnOutputLine:
    // filename tracking, errors, and skip detection ONLY. No byte/percentage
    // bookkeeping at all — that's TelemetryService's job now.
    public class StdoutParser
    {
        private string _sourceRoot;
        private readonly PendingFileTable _pending;
        private readonly InFlightSet _inFlight;
        private readonly System.Collections.Generic.HashSet<string> _reportedSkipPaths = new();

        // Pulls the actual file path out of an ERROR line's message text, e.g.
        // "Copying File C:\Source\locked.txt" -> "C:\Source\locked.txt".
        // Assumes the path runs to the end of the line, which holds for
        // robocopy's error message format.
        private static readonly Regex PathInMessageRegex =
            new Regex(@"(?:[A-Za-z]:\\|\\\\)\S.*$", RegexOptions.Compiled);

        public event EventHandler<string> FolderChanged;
        public event EventHandler<SkippedFileEntry> SkippedFileFound;
        public event EventHandler<string> ErrorOccurred;

        public StdoutParser(string sourceRoot, PendingFileTable pending, InFlightSet inFlight)
        {
            _sourceRoot = sourceRoot;
            _pending = pending;
            _inFlight = inFlight;
        }

        public void SetSourceRoot(string sourceRoot)
        {
            _sourceRoot = sourceRoot;
        }

        public void ParseLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            var errorMatch = RobocopyLineParser.ErrorRegex.Match(line);
            if (errorMatch.Success)
            {
                HandleErrorLine(errorMatch);
                return;
            }

            var parsed = RobocopyLineParser.ParseFileLine(line);
            if (parsed == null) return;

            var isDirectory = parsed.Value.isDirectory;
            var size = parsed.Value.size;
            var path = parsed.Value.path;
            var tag = parsed.Value.tag;

            if (isDirectory)
            {
                FolderChanged?.Invoke(this, path);
                return;
            }

            var relativePath = PathUtil.MakeRelative(_sourceRoot, path);

            if (Array.IndexOf(RobocopyLineParser.SkipTags, tag) >= 0)
            {
                if (_reportedSkipPaths.Contains(relativePath)) return;
                _reportedSkipPaths.Add(relativePath);

                _pending.TryRemove(relativePath, out _);
                _inFlight.TryRemove(relativePath, out _);

                var entry = new SkippedFileEntry(
                    System.IO.Path.GetFileName(path),
                    System.IO.Path.GetDirectoryName(path) + "\\",
                    size,
                    "Skipped by filter");
                SkippedFileFound?.Invoke(this, entry);
                return;
            }

            if (_pending.TryRemove(relativePath, out var pendingEntry))
            {
                _inFlight.Add(relativePath, new InFlightEntry
                {
                    RelativePath = relativePath,
                    DestinationFullPath = pendingEntry.DestinationFullPath,
                    ExpectedSize = pendingEntry.ExpectedSize,
                    ExpectedMTimeUtc = pendingEntry.ExpectedMTimeUtc
                });
            }
        }

        // FIX: this previously only raised ErrorOccurred (logged, never shown to
        // the user) — with RetryCount=0, every failed file ONLY produces an
        // ERROR line (no *EXTRA/Mismatch tag), so skip entries were never being
        // created at all. Now the file path is pulled from the error message
        // itself and a SkippedFileEntry is raised, same as the tag-based path.
        private void HandleErrorLine(Match errorMatch)
        {
            var errorCode = int.Parse(errorMatch.Groups[1].Value);
            var message = errorMatch.Groups[2].Value.Trim();
            ErrorOccurred?.Invoke(this, message);

            var pathMatch = PathInMessageRegex.Match(message);
            if (!pathMatch.Success) return; // couldn't identify which file — nothing to add to the list

            var errorPath = pathMatch.Value.Trim();
            var relativePath = PathUtil.MakeRelative(_sourceRoot, errorPath);

            if (_reportedSkipPaths.Contains(relativePath)) return;
            _reportedSkipPaths.Add(relativePath);

            long size = 0;
            // File may have already been promoted to in-flight (started, then
            // failed) or still be sitting in pending (failed before it ever
            // got a "New File" announce) — check both.
            if (_inFlight.TryRemove(relativePath, out var inFlightEntry))
                size = inFlightEntry.ExpectedSize;
            else if (_pending.TryRemove(relativePath, out var pendingEntry))
                size = pendingEntry.ExpectedSize;

            var entry = new SkippedFileEntry(
                System.IO.Path.GetFileName(errorPath),
                System.IO.Path.GetDirectoryName(errorPath) + "\\",
                size,
                MapReason(errorCode));

            SkippedFileFound?.Invoke(this, entry);
        }

        // Maps robocopy's Win32 error code to a readable reason — previously
        // every error was hardcoded as "Access Denied" regardless of the real
        // cause; now it reflects what actually happened.
        private static string MapReason(int errorCode) => errorCode switch
        {
            5 => "Access Denied",
            32 => "File In Use",
            3 => "Path Not Found",
            112 => "Disk Full",
            _ => $"Error {errorCode}"
        };
    }
}
