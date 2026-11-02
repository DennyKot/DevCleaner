using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DevCleaner.Core.Abstractions;

[assembly: SupportedOSPlatform("windows")]

namespace DevCleaner.Platform.Windows;

public sealed class WindowsPlatformServices(string backupDirectory) : IPlatformServices
{
    public IRecycleBin RecycleBin { get; } = new WindowsRecycleBin();
    public IFileLockDetector LockDetector { get; } = new RestartManagerLockDetector();
    public IReadOnlyList<ICleanupProvider> Providers { get; } = [new RegistryProvider(backupDirectory)];
}

/// <summary>Moves items to the Recycle Bin via SHFileOperationW (64-bit layout).</summary>
public sealed partial class WindowsRecycleBin : IRecycleBin
{
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4;
    private const ushort FOF_NOCONFIRMATION = 0x10;
    private const ushort FOF_ALLOWUNDO = 0x40;
    private const ushort FOF_NOERRORUI = 0x400;

    // Without this, items too large for the bin would be silently deleted permanently.
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    public bool IsSupported => Environment.Is64BitProcess;

    public unsafe void Recycle(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;
        if (!IsSupported)
            throw new PlatformNotSupportedException("Recycle Bin requires a 64-bit process");

        var from = string.Join('\0', paths) + "\0\0";
        fixed (char* pFrom = from)
        {
            var op = new SHFILEOPSTRUCTW
            {
                wFunc = FO_DELETE,
                pFrom = pFrom,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
            };
            var rc = SHFileOperationW(&op);
            if (op.fAnyOperationsAborted != 0)
                throw new OperationCanceledException("Recycle operation was aborted");
            if (rc != 0)
                throw new IOException($"SHFileOperation failed with code 0x{rc:X}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SHFILEOPSTRUCTW
    {
        public nint hwnd;
        public uint wFunc;
        public char* pFrom;
        public char* pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public nint hNameMappings;
        public char* lpszProgressTitle;
    }

    [LibraryImport("shell32.dll")]
    private static unsafe partial int SHFileOperationW(SHFILEOPSTRUCTW* lpFileOp);
}
