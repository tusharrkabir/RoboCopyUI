using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace RoboCopyUI
{
    public class PendingEntry
    {
        public long ExpectedSize;
        public DateTime ExpectedMTimeUtc;
        public string DestinationFullPath;
    }

    public class PendingFileTable
    {
        private readonly ConcurrentDictionary<string, PendingEntry> _entries = new();

        public void Add(string relativePath, PendingEntry entry) => _entries[relativePath] = entry;

        public bool TryRemove(string relativePath, out PendingEntry entry) =>
            _entries.TryRemove(relativePath, out entry);

        public bool TryGet(string relativePath, out PendingEntry entry) =>
            _entries.TryGetValue(relativePath, out entry);

        public int Count => _entries.Count;

        public IEnumerable<KeyValuePair<string, PendingEntry>> Snapshot() => _entries;
    }
}