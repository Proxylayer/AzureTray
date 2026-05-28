using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AzureTray;

// Per-user single-instance guard. The `Local\` namespace prefix scopes
// the mutex to the current Windows logon session, so two different users
// on the same machine can each have their own tray running — but the
// same user double-clicking the shortcut twice gets only one instance.
//
// Acquired is true when this process took ownership of the mutex; false
// when an existing instance already holds it. Dispose releases the
// mutex if we owned it; if we didn't, Dispose is a no-op (no risk of
// nuking the running instance's lock).
//
// Crash safety: if a previous holder exited without releasing (process
// kill, OS crash), the OS abandons the mutex and the next acquirer
// gets it through AbandonedMutexException — we treat that as success.
internal sealed class SingleInstanceLock : IDisposable
{
    // Reverse-DNS form keeps the name from colliding with another app's
    // mutex. Bumping the suffix on a breaking-rename is safe; existing
    // installs holding the old name won't block the new name's lock.
    private const string BaseMutexName = @"Local\Proxylayer.AzureTray.SingleInstance";

    private readonly Mutex _mutex;
    private bool _disposed;

    public bool Acquired { get; }

    public SingleInstanceLock()
    {
        _mutex = new Mutex(initiallyOwned: false, name: ResolveMutexName());
        try
        {
            Acquired = _mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // Previous holder crashed; ownership transfers to us.
            Acquired = true;
        }
    }

    // An isolated dev/test instance (AppPaths.DataRootOverrideEnvVar set) gets
    // its own lock keyed to the override root, so it neither blocks nor is
    // blocked by the real per-user install. Unset → the shared production name.
    private static string ResolveMutexName()
    {
        var overrideRoot = Environment.GetEnvironmentVariable(AppPaths.DataRootOverrideEnvVar);
        if (string.IsNullOrWhiteSpace(overrideRoot)) return BaseMutexName;

        var key = Path.GetFullPath(overrideRoot).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return $"{BaseMutexName}.{hash}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Acquired)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException)
            {
                // Thread that owned it isn't this one — can happen if a
                // background thread somehow ended up running Dispose.
                // Safe to ignore; the mutex is released when the process
                // exits anyway.
            }
        }
        _mutex.Dispose();
    }
}
