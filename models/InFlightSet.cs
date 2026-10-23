using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace RoboCopyUI
{
    public class InFlightEntry
    {
        public string RelativePath;
        public string DestinationFullPath;
        public long ExpectedSize;
        public DateTime ExpectedMTimeUtc;
    }

    public class InFlightSet
    {
        private readonly ConcurrentDictionary<string, InFlightEntry> _entries = new();

        public void Add(string relativePath, InFlightEntry entry) => _entries[relativePath] = entry;

        public bool TryRemove(string relativePath, out InFlightEntry entry) =>
            _entries.TryRemove(relativePath, out entry);

        public ICollection<InFlightEntry> Values => _entries.Values;

        public int Count => _entries.Count;
    }
}