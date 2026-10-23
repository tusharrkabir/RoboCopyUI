using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RoboCopyUI
{
    public enum DriveTypeCategory
    {
        HDD,
        SSD,
        NVMe
    }

    public static class DriveTypeDetector
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct STORAGE_PROPERTY_QUERY
        {
            public uint PropertyId; // 7 = StorageDeviceSeekPenaltyProperty, 0 = StorageDeviceProperty
            public uint QueryType;  // 0 = PropertyStandardQuery
            public byte AdditionalParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
        {
            public uint Version;
            public uint Size;
            [MarshalAs(UnmanagedType.U1)]
            public bool IncursSeekPenalty;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STORAGE_DEVICE_DESCRIPTOR
        {
            public uint Version;
            public uint Size;
            public byte DeviceType;
            public byte DeviceTypeModifier;
            [MarshalAs(UnmanagedType.U1)]
            public bool RemovableMedia;
            [MarshalAs(UnmanagedType.U1)]
            public bool CommandQueueing;
            public uint VendorIdOffset;
            public uint ProductIdOffset;
            public uint ProductRevisionOffset;
            public uint SerialNumberOffset;
            public int BusType; // 17 (0x11) = BusTypeNvme, 11 (0x0B) = BusTypeSata, 7 = BusTypeUsb
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            ref STORAGE_PROPERTY_QUERY lpInBuffer,
            uint nInBufferSize,
            ref DEVICE_SEEK_PENALTY_DESCRIPTOR lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            ref STORAGE_PROPERTY_QUERY lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

        public static DriveTypeCategory DetectDriveCategory(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return DriveTypeCategory.SSD;
                string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
                root = root.TrimEnd('\\', ':');
                if (string.IsNullOrEmpty(root)) return DriveTypeCategory.SSD;

                string devicePath = $@"\\.\{root}:";
                using SafeFileHandle hDevice = CreateFile(
                    devicePath,
                    0, // 0 = Query access without requiring administrator privilege
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (!hDevice.IsInvalid)
                {
                    // 1. Check Seek Penalty (Rotational vs Solid-State)
                    STORAGE_PROPERTY_QUERY seekQuery = new STORAGE_PROPERTY_QUERY
                    {
                        PropertyId = 7, // StorageDeviceSeekPenaltyProperty
                        QueryType = 0,  // PropertyStandardQuery
                        AdditionalParameters = 0
                    };

                    DEVICE_SEEK_PENALTY_DESCRIPTOR seekResult = new DEVICE_SEEK_PENALTY_DESCRIPTOR();

                    if (DeviceIoControl(
                        hDevice,
                        IOCTL_STORAGE_QUERY_PROPERTY,
                        ref seekQuery,
                        (uint)Marshal.SizeOf(seekQuery),
                        ref seekResult,
                        (uint)Marshal.SizeOf(seekResult),
                        out _,
                        IntPtr.Zero))
                    {
                        if (seekResult.IncursSeekPenalty)
                        {
                            return DriveTypeCategory.HDD;
                        }
                    }

                    // 2. Solid state drive detected — query Device Property for BusType (NVMe vs SATA/USB)
                    STORAGE_PROPERTY_QUERY devQuery = new STORAGE_PROPERTY_QUERY
                    {
                        PropertyId = 0, // StorageDeviceProperty
                        QueryType = 0,  // PropertyStandardQuery
                        AdditionalParameters = 0
                    };

                    uint bufferSize = 1024;
                    IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
                    try
                    {
                        if (DeviceIoControl(
                            hDevice,
                            IOCTL_STORAGE_QUERY_PROPERTY,
                            ref devQuery,
                            (uint)Marshal.SizeOf(devQuery),
                            buffer,
                            bufferSize,
                            out _,
                            IntPtr.Zero))
                        {
                            STORAGE_DEVICE_DESCRIPTOR desc = Marshal.PtrToStructure<STORAGE_DEVICE_DESCRIPTOR>(buffer);
                            
                            // BusTypeNvme = 17 (0x11). Some NVMe RAID controllers report 8 (BusTypeRAID) or 1 (BusTypeScsi)
                            if (desc.BusType == 17)
                            {
                                return DriveTypeCategory.NVMe;
                            }

                            // Check vendor/product strings in buffer if present
                            if (desc.ProductIdOffset > 0 && desc.ProductIdOffset < bufferSize)
                            {
                                string prodId = Marshal.PtrToStringAnsi(IntPtr.Add(buffer, (int)desc.ProductIdOffset)) ?? string.Empty;
                                if (prodId.IndexOf("NVMe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    prodId.IndexOf("NVM", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    return DriveTypeCategory.NVMe;
                                }
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }

                    // Default non-rotational drive to standard SATA/USB SSD
                    return DriveTypeCategory.SSD;
                }
            }
            catch
            {
                // Fallback for network drives or unmapped paths
            }

            return DriveTypeCategory.SSD;
        }

        public static (int OptimalThreads, bool RecommendUnbuffered) GetOptimizedSettings(
            string sourcePath, string destPath, long totalBytes, int totalFiles, long maxFileBytes = 0, int smallFiles = 0)
        {
            var srcType = DetectDriveCategory(sourcePath);
            var dstType = DetectDriveCategory(destPath);

            long avgFileSize = totalFiles > 0 ? totalBytes / totalFiles : 0;

            // If either source or destination is a mechanical HDD, multi-threading must be forced to single-threaded (/MT:1).
            // Multi-threading on mechanical drives causes aggressive head thrashing, disk queue lockup, and speed collapse.
            bool containsHDD = (srcType == DriveTypeCategory.HDD || dstType == DriveTypeCategory.HDD);
            bool isAllNVMe = (srcType == DriveTypeCategory.NVMe && dstType == DriveTypeCategory.NVMe);

            int threads;
            if (containsHDD)
            {
                // Mechanical HDD: force single-threaded /MT:1 to prevent head thrashing
                threads = 1;
            }
            else
            {
                // Solid-State Storage (SSD / NVMe):
                // Scale multi-threading dynamically based on file count, average file size, and drive capabilities
                int files = Math.Max(1, totalFiles);
                double smallRatio = smallFiles > 0 ? (double)smallFiles / files : (avgFileSize <= 8 * 1024 * 1024 ? 0.8 : 0.2);

                if (files >= 5000 || (files >= 1000 && (avgFileSize <= 2 * 1024 * 1024 || smallRatio >= 0.85)))
                {
                    // Mass / Tiny files: NVMe gets maximum 128 threads, SATA SSD gets 96 threads
                    threads = isAllNVMe ? 128 : 96;
                }
                else if (files >= 1000 || (files >= 500 && (avgFileSize <= 8 * 1024 * 1024 || smallRatio >= 0.7)))
                {
                    // High file counts / small files: NVMe 96 threads, SATA SSD 64 threads
                    threads = isAllNVMe ? 96 : 64;
                }
                else if (files >= 100)
                {
                    // Moderate file counts: NVMe 64 threads, SATA SSD 32 threads
                    threads = isAllNVMe ? 64 : 32;
                }
                else
                {
                    // Small file counts / large media files (<100 files)
                    if (maxFileBytes >= (1024L * 1024 * 1024) || avgFileSize >= (256L * 1024 * 1024))
                    {
                        threads = isAllNVMe ? 32 : 16;
                    }
                    else
                    {
                        threads = isAllNVMe ? 32 : 16;
                    }
                }
            }

            // Unbuffered I/O (/J) bypasses OS File Cache dirty page flushing.
            // Optimal for files larger than 32MB on solid state drives.
            bool unbuffered = !containsHDD && (avgFileSize > (32 * 1024 * 1024));

            return (threads, unbuffered);
        }

        public static int GetOptimizedDeleteThreads(
            System.Collections.Generic.IEnumerable<string> paths,
            long totalBytes = 0,
            int totalFiles = 0,
            long maxFileBytes = 0,
            int smallFiles = 0,
            bool isMassiveFolder = false)
        {
            try
            {
                bool anyHdd = false;
                bool allNvme = true;
                int pathCount = 0;

                foreach (var path in paths)
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    pathCount++;
                    var cat = DetectDriveCategory(path);
                    if (cat == DriveTypeCategory.HDD)
                    {
                        anyHdd = true;
                        break;
                    }
                    if (cat != DriveTypeCategory.NVMe)
                    {
                        allNvme = false;
                    }
                }

                if (anyHdd)
                {
                    // Mechanical HDD: force single-threaded /MT:1 to prevent aggressive head thrashing
                    return 1;
                }

                bool isNVMe = pathCount > 0 && allNvme;

                // If pre-scan timed out or folder is massive (thousands of items)
                if (isMassiveFolder || totalFiles >= 5000)
                {
                    return isNVMe ? 128 : 96;
                }

                int files = Math.Max(1, totalFiles);
                long avgFileSize = totalFiles > 0 ? totalBytes / totalFiles : 0;
                double smallRatio = smallFiles > 0 ? (double)smallFiles / files : (avgFileSize <= 8 * 1024 * 1024 ? 0.8 : 0.2);

                if (files >= 1000 || (files >= 500 && (avgFileSize <= 8 * 1024 * 1024 || smallRatio >= 0.7)))
                {
                    return isNVMe ? 96 : 64;
                }

                if (files >= 100)
                {
                    return isNVMe ? 64 : 32;
                }

                if (maxFileBytes >= (1024L * 1024 * 1024) || avgFileSize >= (256L * 1024 * 1024))
                {
                    return isNVMe ? 32 : 16;
                }

                return isNVMe ? 64 : 32;
            }
            catch
            {
                return 64;
            }
        }

        public static int GetOptimizedDeleteThreads(string path)
        {
            return GetOptimizedDeleteThreads(new[] { path });
        }
    }
}

