// An object a group of objects that refer to each other frees has a finalizer that takes a lock of the runtime's
// wait subsystem (a SafeHandle's, through the task machinery), while the timer thread holds that lock and waits for
// the runtime's own: the group's finalizers and releases run after the runtime lock is given back, never under it
// (they ran under it and the two threads waited for each other for ever, about once in five hundred runs of a
// program with timers and abandoned tasks, every time in this loop).
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
static class P
{
    static async Task<int> V(int v, int ms = 1) { await Task.Delay(ms); return v; }
    // (WhenAny's other task is far off, 2 s where it was 50 ms: which of two timers that are both due fires first is not
    // .NET's to promise. A timer thread that comes 49 ms late finds both due in one pass of
    // TimerQueue.FireNextTimers, which hands the 50 ms one to the pool before it fires the 1 ms one itself (Timer.cs,
    // timerToFireOnThisThread: the timer made last is found first), and either callback may run first; the sum was
    // 100 less in one loaded run of two hundred here. Not seen in 400 loaded runs of .NET (tools/oracle-load.sh):
    // by .NET's source, not by its output. The first to complete is still the one WhenAny must give.)
    static async Task<int> R() { var first = await await Task.WhenAny(V(100, 2000), V(200, 1)); return first + await V(7); }
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        long sum = 0;
        for (int i = 0; i < 150; i++) { var a = V(100, 50); sum += V(200, 1).Result + R().Result; }
        Console.WriteLine(sum);
    }
}
