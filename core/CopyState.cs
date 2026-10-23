namespace RoboCopyUI
{
    /// <summary>
    /// Every state the copy operation can be in.
    /// MainWindow.UpdateUI() is the single place that maps a state to what the
    /// buttons/labels look like — nothing else should branch on this directly.
    /// </summary>
    public enum CopyState
    {
        Scanning,
        Copying,
        Paused,
        Completed,
        Cancelled,
        Error
    }
}
