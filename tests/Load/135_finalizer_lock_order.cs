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
    static async Task<int> R() { var first = await await Task.WhenAny(V(100, 50), V(200, 1)); return first + await V(7); }
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        long sum = 0;
        for (int i = 0; i < 150; i++) { var a = V(100, 50); sum += V(200, 1).Result + R().Result; }
        Console.WriteLine(sum);
    }
}
