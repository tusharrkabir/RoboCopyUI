using System.Diagnostics;
using System.IO;

namespace RoboCopyUI;

public static class RobocopyCommandBuilder
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();
    public static string FormatRobocopyPath(string path) => Quote(Path.GetFullPath(path));
    private static string Quote(string value) => "\"" + value.TrimEnd('\\') + (value.EndsWith('\\') ? "\\\\" : "") + "\"";

    public static IReadOnlyList<string> BuildArgumentList(RobocopyBatchItem batch, RobocopyOptions options,
        bool listOnly, List<CollisionEntry>? excludedEntries = null, bool forceOverwriteSame = false)
    {
        var args = new List<string> { Path.GetFullPath(batch.SourceDir), Path.GetFullPath(batch.DestDir) };
        args.AddRange(batch.FileSpecs.Count > 0 ? batch.FileSpecs : [options.FileMask]);
        if (batch.IsDirectoryCopy) args.Add("/E");
        args.AddRange(["/BYTES", "/FP", "/NJH", "/NJS", "/NDL", "/NC", "/NP", "/XJ", "/XJD", "/XJF",
            $"/R:{(listOnly ? 0 : options.RetryCount)}", $"/W:{(listOnly ? 0 : options.WaitSeconds)}", $"/MT:{options.ThreadCount}"]);
        if (options.UnbufferedIO) args.Add("/J");
        if (options.FullPreservation) args.AddRange(["/ZB", "/COPYALL", "/DCOPY:DAT"]);
        else args.Add(options.PreserveTimestamps ? "/DCOPY:T" : "/NODCOPY");
        if (forceOverwriteSame) args.AddRange(["/IS", "/IT"]);
        if (excludedEntries != null)
        {
            var files = excludedEntries.Where(e => !e.IsDirectory).Select(e => e.SourceFullPath).ToList();
            var dirs = excludedEntries.Where(e => e.IsDirectory).Select(e => e.SourceFullPath).ToList();
            if (files.Count > 0) { args.Add("/XF"); args.AddRange(files); }
            if (dirs.Count > 0) { args.Add("/XD"); args.AddRange(dirs); }
        }
        if (listOnly) args.Add("/L");
        return args;
    }

    public static ProcessStartInfo CreateStartInfo(RobocopyBatchItem batch, RobocopyOptions options,
        bool listOnly, List<CollisionEntry>? excludedEntries = null, bool forceOverwriteSame = false)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "robocopy.exe"))
        {
            RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.GetEncoding((int)GetOEMCP()),
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var arg in BuildArgumentList(batch, options, listOnly, excludedEntries, forceOverwriteSame))
            start.ArgumentList.Add(arg);
        return start;
    }

    public static string BuildArguments(RobocopyBatchItem batch, RobocopyOptions options, bool listOnly,
        List<CollisionEntry>? excludedEntries = null, bool forceOverwriteSame = false) =>
        string.Join(" ", BuildArgumentList(batch, options, listOnly, excludedEntries, forceOverwriteSame)
            .Select(a => a.StartsWith('/') ? a : Quote(a)));

    public static string BuildArguments(string source, string destination, RobocopyOptions options, bool listOnly,
        List<CollisionEntry>? excludedEntries = null, bool forceOverwriteSame = false)
    {
        bool directory = Directory.Exists(source);
        return BuildArguments(new RobocopyBatchItem
        {
            SourceDir = directory ? source : Path.GetDirectoryName(source)!, DestDir = destination,
            FileSpecs = directory ? [options.FileMask] : [Path.GetFileName(source)], IsDirectoryCopy = directory
        }, options, listOnly, excludedEntries, forceOverwriteSame);
    }
}
