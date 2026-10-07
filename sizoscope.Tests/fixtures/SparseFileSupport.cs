using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sizoscope.Tests.Fixtures;

internal static class SparseFileSupport
{
    private const uint SetSparseControl = 0x000900C4;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInformation
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint control,
        IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out FileStandardInformation information, uint size);

    public static void MarkSparse(FileStream stream)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The >2GiB regression requires verified Windows sparse storage.");
        if (!DeviceIoControl(stream.SafeFileHandle, SetSparseControl, IntPtr.Zero, 0, IntPtr.Zero, 0,
            out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Sparse marking failed; refusing >2GiB extension.");
        if ((File.GetAttributes(stream.Name) & FileAttributes.SparseFile) == 0)
            throw new IOException("Sparse attribute was not established; refusing >2GiB extension.");
    }

    public static long AllocatedBytes(FileStream stream)
    {
        stream.Flush();
        if (!GetFileInformationByHandleEx(stream.SafeFileHandle, 1, out var information,
            (uint)Marshal.SizeOf<FileStandardInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot verify sparse physical allocation.");
        return information.AllocationSize;
    }

    public static void VerifyCapability(string exclusivelyOwnedPath)
    {
        using var probe = new FileStream(exclusivelyOwnedPath, FileMode.CreateNew, FileAccess.ReadWrite);
        MarkSparse(probe); // Must succeed before any extension; SetLength alone is not sparse.
        if (AllocatedBytes(probe) > 1024 * 1024)
            throw new IOException("Unexpected sparse probe allocation.");
        probe.SetLength(2_147_487_744L);
        if (AllocatedBytes(probe) > 1024 * 1024)
            throw new IOException("Volume failed sparse allocation verification.");
    }
}
