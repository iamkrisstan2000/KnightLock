using System;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace KnightLock.Services;

/// <summary>
/// Hands memory back to Windows after the vault engine has finished with it.
///
/// Sealing or opening a container streams the payload through 64 KB cipher chunks and 128 KB copy
/// buffers, so a large vault pushes the managed heap — and its large object heap — well above the
/// app's idle footprint. The GC is happy to keep that memory reserved for reuse, which is the right
/// call for a server and the wrong one for a desktop app: Task Manager keeps showing the peak long
/// after the operation ended, and on a laptop those pages stay resident in RAM.
///
/// So after a heavy operation (progress overlay closing, window losing focus, every vault locked)
/// we compact the large object heap once and ask Windows to trim the working set. Both steps are
/// cheap at that point because nothing is being drawn, and both run off the UI thread.
///
/// Deliberately *not* a timer: trimming on an idle tick would keep pulling pages back out from under
/// a window the user is about to interact with. The calls are debounced instead, so a burst of
/// operations — create, unlock, lock in quick succession — costs one trim.
/// </summary>
public static class MemoryTrimmer
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(3);

    private static long _lastTrimTicks;
    private static int _running;

    // psapi!EmptyWorkingSet + kernel32!GetCurrentProcess (the pseudo-handle is always valid).
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// Releases the memory a finished vault operation left behind. Safe to call from anywhere,
    /// returns immediately, and never blocks the caller.
    /// </summary>
    public static void Release()
    {
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastTrimTicks) < MinimumInterval.TotalMilliseconds) return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

        Volatile.Write(ref _lastTrimTicks, now);

        Task.Run(() =>
        {
            try
            {
                // Compact the LOH once (all those byte buffers are large objects), collect, then
                // let Windows take the freed pages back.
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: true, compacting: true);

                EmptyWorkingSet(GetCurrentProcess());
            }
            catch
            {
                // Trimming is an optimisation; never let it surface as an error.
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        });
    }
}
