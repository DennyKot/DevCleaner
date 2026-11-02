using System.Runtime.InteropServices;
using DevCleaner.Core.Abstractions;

namespace DevCleaner.Platform.Windows;

/// <summary>Uses the Windows Restart Manager to find processes holding a file open.</summary>
public sealed partial class RestartManagerLockDetector : IFileLockDetector
{
    private const int ERROR_MORE_DATA = 234;
    private const int CCH_RM_SESSION_KEY = 32;

    public unsafe IReadOnlyList<string> GetLockingProcesses(string path)
    {
        var key = stackalloc char[CCH_RM_SESSION_KEY + 1];
        if (RmStartSession(out var session, 0, key) != 0)
            return [];
        try
        {
            fixed (char* p = path)
            {
                var files = stackalloc char*[1];
                files[0] = p;
                if (RmRegisterResources(session, 1, files, 0, null, 0, null) != 0)
                    return [];
            }

            uint count = 0;
            var rc = RmGetList(session, out var needed, ref count, null, out _);
            if (rc != ERROR_MORE_DATA || needed == 0)
                return [];

            var infos = new RM_PROCESS_INFO[needed];
            count = needed;
            fixed (RM_PROCESS_INFO* pi = infos)
            {
                if (RmGetList(session, out _, ref count, pi, out _) != 0)
                    return [];
                var names = new List<string>((int)count);
                for (var i = 0; i < count; i++)
                    names.Add(new string(pi[i].strAppName));
                return names;
            }
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public uint ProcessStartTimeLow;
        public uint ProcessStartTimeHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        public fixed char strAppName[256];
        public fixed char strServiceShortName[64];
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        public int bRestartable;
    }

    [LibraryImport("rstrtmgr.dll")]
    private static unsafe partial int RmStartSession(out uint pSessionHandle, int dwSessionFlags, char* strSessionKey);

    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmEndSession(uint pSessionHandle);

    [LibraryImport("rstrtmgr.dll")]
    private static unsafe partial int RmRegisterResources(uint pSessionHandle, uint nFiles, char** rgsFilenames, uint nApplications, void* rgApplications, uint nServices, char** rgsServiceNames);

    [LibraryImport("rstrtmgr.dll")]
    private static unsafe partial int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, RM_PROCESS_INFO* rgAffectedApps, out uint lpdwRebootReasons);
}
