using System.Diagnostics;
using System.IO;

namespace RoboCopyUI;

public static class CollisionRenamer
{
    public static string RenameAndCopy(CollisionEntry entry, RobocopyOptions options, Func<bool>? cancelled = null, Action<string>? report = null)
    {
        var destination = ReserveUniquePath(entry.DestFullPath,entry.IsDirectory);
        var staging = Path.Combine(Path.GetDirectoryName(destination)!, ".robocopyui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var batch = new RobocopyBatchItem
        {
            SourceDir = entry.IsDirectory ? entry.SourceFullPath : Path.GetDirectoryName(entry.SourceFullPath)!,
            DestDir = staging,
            FileSpecs = entry.IsDirectory ? ["*.*"] : [Path.GetFileName(entry.SourceFullPath)],
            IsDirectoryCopy = entry.IsDirectory
        };
        TransferSafety.Validate([batch]);
        var startInfo = RobocopyCommandBuilder.CreateStartInfo(batch, options, false);
        using var process = Process.Start(startInfo) ?? throw new IOException(L.T("无法启动 Robocopy。"));
        var output = process.StandardOutput.ReadToEndAsync();
        while (!process.WaitForExit(100))
        {
            if (cancelled?.Invoke() == true)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new OperationCanceledException(L.F("保留两份已取消；未清源。临时内容：{0}", staging));
            }
        }
        var log = output.GetAwaiter().GetResult();
        report?.Invoke($"{batch.SourceDir} → {batch.DestDir}; exit={process.ExitCode}; {string.Join(" ",startInfo.ArgumentList)}\n{log}");
        if (process.ExitCode >= 8)
            throw new IOException(L.F("保留两份失败（{0}）；源保留。临时内容：{1}\n{2}", process.ExitCode, staging, log));
        if (cancelled?.Invoke() == true) throw new OperationCanceledException(L.T("保留两份已取消，源保留。"));
        // Atomic no-overwrite rename on the target volume; a competing task cannot be merged into.
        if (entry.IsDirectory) Directory.Move(staging, destination);
        else
        {
            File.Move(Path.Combine(staging, Path.GetFileName(entry.SourceFullPath)), destination, overwrite: false);
            Directory.Delete(staging, recursive: false);
        }
        return destination;
    }

    private static string ReserveUniquePath(string path,bool isDirectory)
    {
        var directory = Path.GetDirectoryName(path)!;
        var name = isDirectory ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var extension = isDirectory ? string.Empty : Path.GetExtension(path);
        for (var suffix = 1; ; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name} ({suffix}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
}
