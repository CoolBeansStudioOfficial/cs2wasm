using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
static class Program
{
    sealed class Node { public Node Next; public string Name; public List<int> Data = new List<int>(); }
    static Node s_head;
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        // stress: private garbage on every thread, plus objects handed around through shared structures
        var shared = new List<Node>();
        var gate = new object();
        var tasks = Enumerable.Range(0, 8).Select(w => Task.Run(() =>
        {
            int sum = 0;
            for (int i = 0; i < 3000; i++)
            {
                var local = new Node { Name = "n" + i };
                local.Data.Add(i);
                local.Next = new Node { Name = local.Name + "x" };
                sum += local.Next.Name.Length;
                if (i % 50 == 0) lock (gate) { shared.Add(local); }
                if (i % 70 == 0) lock (gate) { if (shared.Count > 0) { var n = shared[shared.Count - 1]; shared.RemoveAt(shared.Count - 1); sum += n.Data[0] >= 0 ? 1 : 0; } }
                if (i % 500 == 0) Interlocked.Exchange(ref s_dummy, i);
                if (i % 333 == 0) { var h = new Node { Name = "head" + w, Next = Volatile.Read(ref s_head) }; Volatile.Write(ref s_head, h); }
            }
            return sum;
        })).ToArray();
        Task.WaitAll(tasks);
        int count = 0;
        for (var n = s_head; n != null; n = n.Next) count++;
        Console.WriteLine(tasks.Sum(t => t.Result) > 0 ? "ok " + (count > 0) + " " + (shared.Count <= 480) : "bad");
    }
    static int s_dummy;
}
