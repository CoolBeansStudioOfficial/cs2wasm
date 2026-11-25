// Ported from tests/Conformance/017_cycle_stress.cs (the old suite): the same program, for the cycle groups (docs/MEMORY-IR.md S2).
// live: 0
// Random mutation of a cyclic object graph (section 4.3): arena merges, uncounted references being overwritten, sweeps
// of long-lived arenas. The program's view of the graph must stay exactly .NET's.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

class Node
{
    static int s_next;
    public readonly int Id = ++s_next;
    public Node A, B;
    public object Tag;
    public List<Node> Out = new();
    public Node[] Arr = new Node[3];
}

static class Program
{
    static Node Pick(Random r, Node[] pool) => r.Next(5) == 0 ? null : pool[r.Next(pool.Length)];

    static (int count, long sum) Walk(Node[] pool)
    {
        var seen = new HashSet<Node>();
        var stack = new Stack<Node>();
        foreach (var p in pool) if (p != null) stack.Push(p);
        long sum = 0;
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!seen.Add(n)) continue;
            sum += n.Id * (long)(seen.Count % 7 + 1);
            if (n.A != null) stack.Push(n.A);
            if (n.B != null) stack.Push(n.B);
            if (n.Tag is Node t) stack.Push(t);
            foreach (var o in n.Out) stack.Push(o);
            foreach (var o in n.Arr) if (o != null) stack.Push(o);
        }
        return (seen.Count, sum);
    }

    static void Main()
    {
        var r = new Random(12345);
        var pool = new Node[24];
        for (int i = 0; i < pool.Length; i++) pool[i] = new Node();
        for (int step = 1; step <= 60000; step++)
        {
            var n = pool[r.Next(pool.Length)];
            switch (r.Next(11))
            {
                case 0: n.A = Pick(r, pool); break;
                case 1: n.B = Pick(r, pool); break;
                case 2: n.Tag = r.Next(3) == 0 ? "tag" : Pick(r, pool); break;
                case 3: { var m = Pick(r, pool); if (m != null) n.Out.Add(m); break; }
                case 4: if (n.Out.Count > 0) n.Out.RemoveAt(r.Next(n.Out.Count)); break;
                case 5: n.Arr[r.Next(3)] = Pick(r, pool); break;
                case 6: pool[r.Next(pool.Length)] = new Node(); break;     // drop a root
                case 7: if (r.Next(4) == 0) n.Out.Clear(); break;
                case 8: { var c = new Node(); c.A = n; c.B = c; n.Out.Add(c); break; } // child with back and self pointer
                case 9: n.Arr = (Node[])n.Arr.Clone(); break;
                case 10: { var m = n.A; if (m != null) { n.A = null; pool[r.Next(pool.Length)] = m; } break; } // move to a root
            }
            if (step % 5000 == 0)
            {
                var (count, sum) = Walk(pool);
                Console.WriteLine("step " + step + ": " + count + " reachable, checksum " + sum);
            }
        }
    }
}
