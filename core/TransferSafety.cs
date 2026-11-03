using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace RoboCopyUI;

/// <summary>Frozen manifest. Neither robocopy's exit code nor progress authorizes deletion.</summary>
public sealed class TransferSafety
{
    private sealed record FileStamp(string Source, string Target, uint Volume, ulong Id,
        long Length, long Written, long Created, string Hash);
    private readonly List<FileStamp> _files = new();
    private sealed record DirectoryStamp(string Source, string Target, uint Volume, ulong Id, long Written);
    private readonly List<DirectoryStamp> _directories = new();
    public List<string> Warnings { get; } = new();
    private Func<bool> cancelled = () => false;
    private bool forMove = true;

    public static string Normalize(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['*', '?', '"', '\0']) >= 0)
            throw new IOException(L.F("必须提供绝对文件系统路径：{0}", path));
        if (path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\") || path.IndexOf(':', 2) >= 0 ||
            path.Split(['\\', '/']).Any(part => part.EndsWith('.') || part.EndsWith(' ')))
            throw new IOException(L.F("不支持设备路径、命名数据流或末尾点/空格别名：{0}", path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    // Internal Win32 spelling only. Public requests still reject device paths.
    internal static string NativePath(string path) => path.Length<248 ? path :
        path.StartsWith(@"\\") ? @"\\?\UNC\"+path[2..] : @"\\?\"+path;

    private static bool Contains(string parent, string child) =>
        child.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static void RequireNoLinks(string path)
    {
        for (string? part = Normalize(path); part != null; part = Path.GetDirectoryName(part))
        {
            if (!File.Exists(part) && !Directory.Exists(part)) continue;
            var attributes=File.GetAttributes(part);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException(L.F("路径包含链接或重解析点，未执行：{0}", part));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                using var handle=CreateFile(part,0,7,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
                if(handle.IsInvalid)throw new IOException(L.F("无法核验目录：{0}", part),new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
                if(GetFileInformationByHandleEx(handle,23,out var flags,4)&&(flags&1)!=0)
                    throw new IOException(L.F("暂不支持区分大小写的目录，未执行：{0}", part));
            }
        }
    }

    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int kind,out uint flags,uint size);

    public static void Validate(IReadOnlyList<RobocopyBatchItem> batches, Func<bool>? cancelled = null)
    {
        if (batches.Count == 0) throw new IOException(L.T("没有可传输的源项目。"));
        var mappings = new List<(string Source, string Target, bool Directory)>();
        foreach (var batch in batches)
        {
            if(cancelled?.Invoke()==true)throw new OperationCanceledException();
            RequireNoLinks(batch.SourceDir);
            RequireNoLinks(batch.DestDir);
            var source = Normalize(batch.SourceDir);
            var target = Normalize(batch.DestDir);
            if (batch.IsDirectoryCopy)
            {
                if (Path.GetPathRoot(source) == source)
                    throw new IOException(L.T("不支持以整盘或共享根作为选中源。"));
                mappings.Add((source, target, true));
            }
            else
            {
                foreach (var name in batch.FileSpecs)
                {
                    if (Path.GetFileName(name) != name || name.IndexOfAny(['*', '?', ':']) >= 0)
                        throw new IOException(L.T("文件任务必须使用明确文件名。"));
                    var file = Path.Combine(source, name);
                    RequireNoLinks(file);
                    RequireNoLinks(Path.Combine(target, name));
                    mappings.Add((file, Path.Combine(target, name), false));
                }
            }
        }
        foreach (var a in mappings)
        {
            if(cancelled?.Invoke()==true)throw new OperationCanceledException();
            if(mappings.Any(b => b!=a && (Contains(a.Source,b.Source)||Contains(b.Source,a.Source))))
                throw new IOException(L.T("选中的源互相包含，请拆分任务。"));
            foreach (var b in mappings)
                if (Contains(a.Source, b.Target) || Contains(b.Target, a.Source))
                    throw new IOException(L.F("源与目标相同、嵌套或重叠：{0} → {1}", a.Source, b.Target));
            if (mappings.Any(b => b != a &&
                (Contains(a.Target, b.Target) || Contains(b.Target, a.Target))))
                throw new IOException(L.T("多个源的目标映射重叠，请拆分任务。"));
        }
        // Drive letters, SUBST and UNC shares can name the same physical tree.
        // Compare existing objects/ancestors by volume serial and file ID, not path text alone.
        var identities=new Dictionary<string,(uint Volume,ulong Id)?>(StringComparer.OrdinalIgnoreCase);
        (uint Volume,ulong Id)? Identity(string path)
        {
            if(cancelled?.Invoke()==true)throw new OperationCanceledException();
            if(identities.TryGetValue(path,out var cached))return cached;
            if(!File.Exists(path)&&!Directory.Exists(path))return identities[path]=null;
            RequireNoLinks(path);
            using var handle=CreateFile(path,0,7,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
            var info=Info(handle,allowDirectory:true,allowLinks:true);
            var id=((ulong)info.FileIndexHigh<<32)|info.FileIndexLow;
            return identities[path]=id==0?null:(info.VolumeSerialNumber,id);
        }
        IEnumerable<string> Parents(string path)
        {for(string? current=Normalize(path);current!=null;current=Path.GetDirectoryName(current))yield return current;}
        var sourceIds=mappings.Select(m=>Identity(m.Source)).Where(i=>i.HasValue).Select(i=>i!.Value).ToHashSet();
        var sourceAncestorIds=mappings.SelectMany(m=>Parents(m.Source)).Select(Identity).Where(i=>i.HasValue).Select(i=>i!.Value).ToHashSet();
        var targetRoots=new List<(uint Volume,ulong Id)>();
        var targetAncestors=new Dictionary<(uint Volume,ulong Id),int>();
        foreach(var mapping in mappings)
        {
            var own=Identity(mapping.Target);
            if(own.HasValue){targetRoots.Add(own.Value);if(sourceAncestorIds.Contains(own.Value))throw new IOException(L.T("目标通过路径别名包含了源，未执行任务。"));}
            foreach(var parent in Parents(mapping.Target))
            {
                var id=Identity(parent);if(!id.HasValue)continue;
                if(sourceIds.Contains(id.Value))throw new IOException(L.T("源与目标通过路径别名相同或嵌套，未执行任务。"));
                targetAncestors[id.Value]=targetAncestors.GetValueOrDefault(id.Value)+1;
            }
        }
        if(targetRoots.Any(id=>targetAncestors.GetValueOrDefault(id)>1))throw new IOException(L.T("多个目标通过路径别名重叠，未执行任务。"));
    }

    public static TransferSafety Freeze(RobocopyBatchItem batch, Func<bool>? cancelled = null, bool forMove = true)
    {
        var manifest = new TransferSafety { cancelled = cancelled ?? (() => false), forMove = forMove };
        if (batch.IsDirectoryCopy) manifest.Walk(batch.SourceDir, batch.DestDir);
        else foreach (var name in batch.FileSpecs)
            manifest.Capture(Path.Combine(batch.SourceDir, name), Path.Combine(batch.DestDir, name));
        return manifest;
    }

    private void Walk(string source, string target)
    {
        if(cancelled())throw new OperationCanceledException();
        RequireNoLinks(source);
        RequireNoLinks(target);
        if (forMove && HasNamedStreams(source)) Warnings.Add(L.F("目录含命名数据流，保留源：{0}", source));
        using (var handle = CreateFile(source, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
        {
            var info = Info(handle, allowDirectory: true);
            _directories.Add(new(source, target, info.VolumeSerialNumber,
                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
                ((long)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime));
        }
        foreach (var item in Directory.EnumerateFileSystemEntries(source))
        {
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
            {
                Warnings.Add(L.F("跳过重解析点：{0}", item));
                continue;
            }
            var dest = Path.Combine(target, Path.GetFileName(item));
            RequireNoLinks(dest);
            if (Directory.Exists(item)) Walk(item, dest);
            else Capture(item, dest);
        }
    }

    private void Capture(string source, string target)
    {
        if(cancelled())throw new OperationCanceledException();
        RequireNoLinks(target);
        if(File.Exists(target))
        {
            // Metadata-only open also works for a deliberately data-locked target.
            using var targetHandle=CreateFile(target,0,7,IntPtr.Zero,3,0x00200000,IntPtr.Zero);
            Info(targetHandle); // Refuse hard-linked destination aliases before any overwrite.
        }
        if (forMove && HasNamedStreams(source)) Warnings.Add(L.F("含命名数据流，尚未完成逐流验证；保留源：{0}", source));
        using var handle = CreateFile(source,forMove ? 0x80000000u : 0,1,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
        var info = Info(handle,allowLinks:!forMove);
        string hash=string.Empty;
        if(forMove){using var file=new FileStream(handle,FileAccess.Read);hash=Hash(file,cancelled);}
        _files.Add(new(source, target, info.VolumeSerialNumber,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, ((long)info.FileSizeHigh<<32)|info.FileSizeLow,
            ((long)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime,
            ((long)info.CreationTime.dwHighDateTime << 32) | (uint)info.CreationTime.dwLowDateTime,
            hash));
    }

    public void RemapTarget(string sourceRoot,string newTarget)
    {
        string TargetFor(string path) => path.Equals(sourceRoot,StringComparison.OrdinalIgnoreCase)
            ? newTarget : Path.Combine(newTarget,Path.GetRelativePath(sourceRoot,path));
        for(var i=0;i<_files.Count;i++)
            if(Contains(sourceRoot,_files[i].Source))_files[i]=_files[i] with {Target=TargetFor(_files[i].Source)};
        for(var i=0;i<_directories.Count;i++)
            if(Contains(sourceRoot,_directories[i].Source))_directories[i]=_directories[i] with {Target=TargetFor(_directories[i].Source)};
    }

    private static string Hash(Stream stream,Func<bool> cancelled)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer=new byte[65536];
        while(true)
        {
            if(cancelled())throw new OperationCanceledException();
            var read=stream.Read(buffer,0,buffer.Length);
            if(read==0)break;
            hash.AppendData(buffer,0,read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Conservative two-phase validation. Handles deny writes/deletes while verified.</summary>
    public IReadOnlyList<string> CleanVerifiedSources(bool fullMetadata, Func<bool> cancelled)
    {
        var problems = new List<string>(Warnings);
        if (problems.Count > 0 || cancelled()) return problems;
        using var sourceDirectories = new HeldHandles();
        using var targetDirectories = new HeldHandles();
        var held = new List<(FileStream Source, FileStream Target, FileStamp Stamp)>();
        try
        {
            foreach (var directory in _directories)
            {
                RequireNoLinks(directory.Source);
                RequireNoLinks(directory.Target);
                if (!Directory.Exists(directory.Target)) throw new IOException(L.F("目标空目录未验证：{0}", directory.Target));
                var sourceHandle=CreateFile(directory.Source,0,3,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
                sourceDirectories.Add(sourceHandle);
                var directoryInfo=Info(sourceHandle,allowDirectory:true);
                if(directoryInfo.VolumeSerialNumber!=directory.Volume ||
                    (((ulong)directoryInfo.FileIndexHigh<<32)|directoryInfo.FileIndexLow)!=directory.Id ||
                    (((long)directoryInfo.LastWriteTime.dwHighDateTime<<32)|(uint)directoryInfo.LastWriteTime.dwLowDateTime)!=directory.Written)
                    throw new IOException(L.F("源目录已变化；整个批次保留源：{0}", directory.Source));
                var targetHandle=CreateFile(directory.Target,0,3,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
                targetDirectories.Add(targetHandle);
                if((Info(targetHandle,allowDirectory:true).FileAttributes&(uint)FileAttributes.Directory)==0)
                    throw new IOException(L.T("目标不再是目录，保留源。"));
                if (HasNamedStreams(directory.Source) || HasNamedStreams(directory.Target))
                    throw new IOException(L.F("目录含命名数据流，源保留：{0}", directory.Source));
                if (fullMetadata && (File.GetAttributes(directory.Source) != File.GetAttributes(directory.Target) ||
                    Directory.GetCreationTimeUtc(directory.Source) != Directory.GetCreationTimeUtc(directory.Target) ||
                    Directory.GetLastWriteTimeUtc(directory.Source) != Directory.GetLastWriteTimeUtc(directory.Target)))
                    throw new IOException(L.F("目录属性或时间戳验证失败，源保留：{0}", directory.Source));
                if (fullMetadata && ReadDirectorySecurity(directory.Source) != ReadDirectorySecurity(directory.Target))
                    throw new IOException(L.F("目录权限验证失败，源保留：{0}", directory.Source));
            }
            VerifyFrozenTree();
            // Verify the entire batch before deleting any source file.
            foreach (var stamp in _files)
            {
                if (cancelled()) return [L.T("任务已取消；保留源。")];
                RequireNoLinks(stamp.Source);
                RequireNoLinks(stamp.Target);
                if (HasNamedStreams(stamp.Source) || HasNamedStreams(stamp.Target))
                    throw new IOException(L.F("含命名数据流，保留源供人工核对：{0}", stamp.Source));
                var source = Open(stamp.Source, deleteAccess: true);
                FileStream? target = null;
                try
                {
                    target = Open(stamp.Target, deleteAccess: false);
                    held.Add((source, target, stamp));
                }
                catch { source.Dispose(); target?.Dispose(); throw; }
                var info = Info(source.SafeFileHandle);
                var written = ((long)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime;
                var created = ((long)info.CreationTime.dwHighDateTime << 32) | (uint)info.CreationTime.dwLowDateTime;
                if (info.VolumeSerialNumber != stamp.Volume ||
                    (((ulong)info.FileIndexHigh << 32) | info.FileIndexLow) != stamp.Id ||
                    source.Length != stamp.Length || written != stamp.Written || created != stamp.Created ||
                    Hash(source,cancelled) != stamp.Hash || Hash(target,cancelled) != stamp.Hash)
                    throw new IOException(L.F("源变化或目标内容验证失败，保留源：{0}", stamp.Source));
                var tolerance=fullMetadata ? TimeSpan.Zero : VolumeCapabilities.Read(stamp.Target).OrdinaryTimestampTolerance;
                if (File.GetAttributes(stamp.Source) != File.GetAttributes(stamp.Target) ||
                    (File.GetCreationTimeUtc(stamp.Source)-File.GetCreationTimeUtc(stamp.Target)).Duration()>tolerance ||
                    (File.GetLastWriteTimeUtc(stamp.Source)-File.GetLastWriteTimeUtc(stamp.Target)).Duration()>tolerance)
                    throw new IOException(L.F("目标属性或时间戳不一致，保留源：{0}", stamp.Source));
                // Audit/owner copying requires privileges. If it cannot be read, retain source.
                if (fullMetadata && ReadSecurity(stamp.Source) != ReadSecurity(stamp.Target))
                    throw new IOException(L.F("目标权限验证失败，保留源：{0}", stamp.Source));
            }
            if (cancelled()) return [L.T("任务已取消；保留源。")];
            VerifyFrozenTree();
            foreach (var pair in held)
            {
                if (cancelled()) { problems.Add(L.T("清理已取消；其余源保留。")); break; }
                var disposition = new FileDisposition { DeleteFile = true };
                if (!SetFileInformationByHandle(pair.Source.SafeFileHandle, 4, ref disposition, 1))
                    problems.Add(L.F("无法删除已验证源：{0} (Win32 {1})", pair.Stamp.Source, Marshal.GetLastWin32Error()));
            }
        }
        catch (Exception ex) { problems.Add(ex.Message); }
        finally
        {
            foreach (var pair in held) { pair.Target.Dispose(); pair.Source.Dispose(); }
        }
        if (problems.Count == 0)
        {
            sourceDirectories.Dispose(); // Release our own deny-delete locks before empty-only deletion.
            foreach (var directory in _directories.OrderByDescending(p => p.Source.Length))
                try
                {
                    RequireNoLinks(directory.Source);
                    using var handle = CreateFile(directory.Source, 0x10000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    var info = Info(handle, allowDirectory: true);
                    if (info.VolumeSerialNumber != directory.Volume ||
                        (((ulong)info.FileIndexHigh << 32) | info.FileIndexLow) != directory.Id)
                        throw new IOException(L.T("源目录标识变化。"));
                    var disposition = new FileDisposition { DeleteFile = true };
                    if (!SetFileInformationByHandle(handle, 4, ref disposition, 1))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); // Nonempty directories stay.
                }
                catch (Exception ex) { problems.Add(L.F("源目录保留：{0}；{1}", directory.Source, ex.Message)); }
        }
        return problems;
    }

    private void VerifyFrozenTree()
    {
        var entries=_files.Select(f=>f.Source).Concat(_directories.Select(d=>d.Source))
            .GroupBy(p=>Path.GetDirectoryName(p)!,StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g=>g.Key,g=>g.ToHashSet(StringComparer.OrdinalIgnoreCase),StringComparer.OrdinalIgnoreCase);
        foreach(var directory in _directories)
        {
            var expected=entries.GetValueOrDefault(directory.Source) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var actual=Directory.EnumerateFileSystemEntries(directory.Source).Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if(!actual.SetEquals(expected))throw new IOException(L.F("源目录增删了项目；整个批次保留源：{0}", directory.Source));
        }
    }

    private sealed class HeldHandles : IDisposable
    {
        private readonly List<SafeFileHandle> handles=new();
        public void Add(SafeFileHandle handle)=>handles.Add(handle);
        public void Dispose(){foreach(var handle in handles)handle.Dispose();handles.Clear();}
    }

    private static string ReadSecurity(string path) => new FileInfo(path).GetAccessControl(
        System.Security.AccessControl.AccessControlSections.All).GetSecurityDescriptorSddlForm(
        System.Security.AccessControl.AccessControlSections.All);
    private static string ReadDirectorySecurity(string path) => new DirectoryInfo(path).GetAccessControl(
        System.Security.AccessControl.AccessControlSections.All).GetSecurityDescriptorSddlForm(
        System.Security.AccessControl.AccessControlSections.All);

    private static FileStream Open(string path, bool deleteAccess)
    {
        var handle = CreateFile(path, 0x80000000u | (deleteAccess ? 0x10000u : 0),
            1, IntPtr.Zero, 3, 0x02200000u, IntPtr.Zero); // OPEN_REPARSE_POINT + BACKUP_SEMANTICS, FILE_SHARE_READ
        if (handle.IsInvalid) { handle.Dispose(); throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), path); }
        return new FileStream(handle, FileAccess.Read);
    }

    private static ByHandleFileInformation Info(SafeFileHandle handle, bool allowDirectory = false, bool allowLinks = false)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if ((info.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
            throw new IOException(L.T("拒绝链接文件。"));
        if (!allowDirectory && !allowLinks && info.NumberOfLinks != 1) throw new IOException(L.T("硬链接文件需要人工核对，保留源。"));
        return info;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }
    private static bool HasNamedStreams(string path)
    {
        var find = FindFirstStream(NativePath(path), 0, out var stream, 0);
        if (find == new IntPtr(-1))
        {
            var code = Marshal.GetLastWin32Error();
            if (code == 38) return false;
            if((code==1||code==50||code==87) && !VolumeCapabilities.Read(path).NamedStreams)return false;
            throw new IOException(L.F("无法核对命名数据流（Win32 {0}），源保留：{1}", code, path));
        }
        try
        {
            do { if (stream.Name != "::$DATA") return true; }
            while (FindNextStream(find, out stream));
            if (Marshal.GetLastWin32Error() != 38) throw new IOException(L.T("枚举数据流未完成，源保留。"));
            return false;
        }
        finally { FindClose(find); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        public long Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name;
    }
    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStream(string path, int level, out StreamData data, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextStream(IntPtr find, out StreamData data);
    [DllImport("kernel32.dll")] private static extern bool FindClose(IntPtr find);
    private static SafeFileHandle CreateFile(string path,uint access,uint sharing,IntPtr security,uint creation,uint flags,IntPtr template)
    {
        var handle=CreateFileNative(NativePath(path),access,sharing,security,creation,flags,template);
        if(handle.IsInvalid){var error=Marshal.GetLastWin32Error();handle.Dispose();throw new System.ComponentModel.Win32Exception(error,path);}
        return handle;
    }
    [DllImport("kernel32.dll", EntryPoint="CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileNative(string path, uint access, uint sharing, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref FileDisposition info, uint size);
}
