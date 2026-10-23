namespace RoboCopyUI
{
    public enum CollisionChoice
    {
        Replace,
        Skip,
        Rename
    }

    // One top-level (source-root) entry that already exists at destination.
    // Only top-level names are checked — matches this engine's model of
    // "copy CONTENTS of source into destination", not per-selected-item queueing.
    public class CollisionEntry
    {
        public string Name;
        public bool IsDirectory;
        public string SourceFullPath;
        public string DestFullPath;
    }
}