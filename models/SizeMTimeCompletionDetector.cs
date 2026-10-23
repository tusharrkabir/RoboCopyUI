using System;
using System.IO;

namespace RoboCopyUI
{
    public class SizeMTimeCompletionDetector : ICompletionDetector
    {
        private static readonly TimeSpan MTimeTolerance = TimeSpan.FromSeconds(2);

        // Completion check happens AFTER robocopy closes its write handle to
        // the file — at that point Windows has already flushed real size/mtime
        // to directory metadata, so a plain FileInfo stat here is reliable.
        public long CheckAndReap(InFlightSet inFlight, out int filesCompleted)
        {
            long bytesCompleted = 0;
            filesCompleted = 0;

            foreach (var entry in inFlight.Values)
            {
                if (!File.Exists(entry.DestinationFullPath)) continue;

                var info = new FileInfo(entry.DestinationFullPath);
                if (info.Length != entry.ExpectedSize) continue;

                var mtimeDiff = (info.LastWriteTimeUtc - entry.ExpectedMTimeUtc).Duration();
                if (mtimeDiff > MTimeTolerance) continue;

                if (inFlight.TryRemove(entry.RelativePath, out _))
                {
                    bytesCompleted += entry.ExpectedSize;
                    filesCompleted++;
                }
            }

            return bytesCompleted;
        }

        // FIX: while a file is STILL being written by robocopy, FileInfo.Length
        // reads cached directory metadata that does NOT update live — it only
        // refreshes once the writer flushes or closes the handle. That's what
        // caused the bar to sit near-0 the whole copy, then jump the full file
        // size at once on completion.
        //
        // Opening a FileStream with FileShare.ReadWrite|Delete and reading its
        // .Length instead queries the file's CURRENT size directly from the
        // open file object (not directory-entry cache) — this reflects real
        // growth while robocopy is still writing, without interfering with
        // robocopy's own write handle.
        public long GetInFlightPartialBytes(InFlightSet inFlight)
        {
            long total = 0;

            foreach (var entry in inFlight.Values)
            {
                try
                {
                    using var stream = new FileStream(
                        entry.DestinationFullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);

                    var liveSize = stream.Length;
                    total += Math.Min(liveSize, entry.ExpectedSize);
                }
                catch (IOException)
                {
                    // File not created yet, or a rare transient lock that even
                    // blocks shared-read for an instant — skip this file for
                    // this tick, it'll be picked up again in 250ms.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same idea — skip for this tick.
                }
            }

            return total;
        }
    }
}