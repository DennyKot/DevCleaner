using DevCleaner.Core.Abstractions;

namespace DevCleaner.Tests;

/// <summary>Platform with no locks and no recycle bin, for permanent-delete engine tests.</summary>
internal sealed class FakePlatform : IPlatformServices
{
    public IRecycleBin RecycleBin => throw new NotSupportedException();
    public IFileLockDetector LockDetector { get; } = new NoLocks();
    public IReadOnlyList<ICleanupProvider> Providers => [];

    private sealed class NoLocks : IFileLockDetector
    {
        public IReadOnlyList<string> GetLockingProcesses(string path) => [];
    }
}
