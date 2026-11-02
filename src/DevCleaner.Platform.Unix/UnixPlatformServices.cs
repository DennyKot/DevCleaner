using DevCleaner.Core.Abstractions;

namespace DevCleaner.Platform.Unix;

/// <summary>Placeholder for Linux/macOS: no trash integration or lock detection yet (permanent delete only).</summary>
public sealed class UnixPlatformServices : IPlatformServices
{
    public IRecycleBin RecycleBin { get; } = new NoRecycleBin();
    public IFileLockDetector LockDetector { get; } = new NoLockDetector();
    public IReadOnlyList<ICleanupProvider> Providers { get; } = [];

    private sealed class NoRecycleBin : IRecycleBin
    {
        public bool IsSupported => false;

        public void Recycle(IReadOnlyList<string> paths) =>
            throw new PlatformNotSupportedException("Trash is not implemented on this OS yet");
    }

    private sealed class NoLockDetector : IFileLockDetector
    {
        public IReadOnlyList<string> GetLockingProcesses(string path) => [];
    }
}
