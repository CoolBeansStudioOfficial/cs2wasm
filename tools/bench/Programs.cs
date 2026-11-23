// Benchmarks for the comparison with NativeAOT (SPEC sections 1, 7.6, 10): the same C# built by il2wasm and by
// NativeAOT. Each returns a checksum, which must be the same on both. tools/bench.mjs builds and runs them.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

static class Bench
{
    /// <summary>Runs the work five times: the best and the first time (microseconds), and the checksum.</summary>
    public static void Time(string name, Func<long> work)
    {
        long best = long.MaxValue, first = 0, check = 0;
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            check = work();
            sw.Stop();
            long us = sw.ElapsedTicks * 1000000 / Stopwatch.Frequency;
            if (i == 0) first = us;
            if (us < best) best = us;
        }
        Console.WriteLine("bench " + name + " best_us=" + best + " first_us=" + first + " check=" + check);
    }

    public static void RunAll()
    {
        Time("dictionary", Programs.DictionaryInts);
        Time("word_count", Programs.WordCount);
        Time("strings", Programs.Strings);
        Time("tree", Programs.Tree);
        Time("list_sort", Programs.ListSort);
        Time("linq", Programs.Linq);
        Time("nbody", Programs.NBody);
        Time("shapes", Programs.Shapes);
        Time("linked_list", Programs.LinkedNodes);
    }
}

abstract class Shape { public abstract double Area(); public virtual int Sides => 0; }
sealed class Circle(double r) : Shape { public override double Area() => 3.141592653589793 * r * r; }
sealed class Rect(double w, double h) : Shape { public override double Area() => w * h; public override int Sides => 4; }
sealed class Tri(double b, double h) : Shape { public override double Area() => 0.5 * b * h; public override int Sides => 3; }

sealed class TreeNode { public TreeNode? Left, Right; public int Value; }
sealed class ListNode { public ListNode? Next; public string Name = ""; public int Value; }

struct Vec3
{
    public double X, Y, Z;
    public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public double LengthSquared => X * X + Y * Y + Z * Z;
}

sealed class Body { public Vec3 Position, Velocity; public double Mass; }

static class Programs
{
    static uint s_seed;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }

    public static long DictionaryInts()
    {
        s_seed = 1;
        var d = new Dictionary<int, int>();
        for (int i = 0; i < 200000; i++) d[(int)(Next() % 100000)] = i;
        long sum = 0;
        for (int i = 0; i < 200000; i++) if (d.TryGetValue((int)(Next() % 150000), out int v)) sum += v;
        for (int i = 0; i < 50000; i++) d.Remove(i * 2);
        foreach (var kv in d) sum += kv.Key ^ kv.Value;
        return sum + d.Count;
    }

    static readonly string[] s_words = "the quick brown fox jumps over the lazy dog while seven wizards quietly judge twelve boxing kangaroos near a frozen river bank".Split(' ');

    public static long WordCount()
    {
        s_seed = 2;
        var counts = new Dictionary<string, List<int>>();
        for (int i = 0; i < 120000; i++)
        {
            string w = s_words[Next() % (uint)s_words.Length];
            if (!counts.TryGetValue(w, out var list)) counts[w] = list = new List<int>();
            list.Add(i);
        }
        long sum = 0;
        foreach (var (word, list) in counts) sum += word.Length * (long)list.Count + list[list.Count / 2];
        return sum;
    }

    public static long Strings()
    {
        s_seed = 3;
        var sb = new StringBuilder();
        for (int i = 0; i < 40000; i++) { sb.Append(s_words[Next() % (uint)s_words.Length]); sb.Append(i % 7 == 0 ? ';' : ' '); sb.Append(i); sb.Append(' '); }
        string text = sb.ToString();
        long sum = text.Length;
        foreach (var part in text.Split(';')) { sum += part.Length; sum += part.IndexOf("fox", StringComparison.Ordinal); }
        for (int i = 0; i < 20000; i++) { string s = "k" + i + ":" + (i * 3).ToString(); sum += s.Length + s[s.Length - 1]; }
        // (not its hash code: .NET's string hash is seeded per process)
        string upper = text.ToUpperInvariant();
        for (int i = 0; i < upper.Length; i += 97) sum += upper[i];
        return sum;
    }

    static TreeNode Build(int depth, ref int counter)
    {
        var n = new TreeNode { Value = counter++ };
        if (depth > 0) { n.Left = Build(depth - 1, ref counter); n.Right = Build(depth - 1, ref counter); }
        return n;
    }

    static long Walk(TreeNode? n) => n == null ? 0 : n.Value + Walk(n.Left) + Walk(n.Right);

    public static long Tree()
    {
        long sum = 0;
        for (int round = 0; round < 12; round++)
        {
            int counter = round;
            var root = Build(15, ref counter);
            sum += Walk(root);
            // replace a subtree: the old one is freed
            root.Left = Build(10, ref counter);
            sum += Walk(root);
        }
        return sum;
    }

    public static long ListSort()
    {
        s_seed = 5;
        var list = new List<int>();
        for (int i = 0; i < 300000; i++) list.Add((int)Next());
        list.Sort();
        long sum = 0;
        for (int i = 0; i < 100000; i++) { int ix = list.BinarySearch((int)Next()); sum += ix < 0 ? ~ix : ix; }
        var names = new List<string>();
        for (int i = 0; i < 30000; i++) names.Add(s_words[Next() % (uint)s_words.Length] + (Next() % 1000));
        names.Sort(StringComparer.Ordinal);
        return sum + list[1234] + names[777].Length + names[names.Count - 1][0];
    }

    public static long Linq()
    {
        var data = Enumerable.Range(0, 200000).Select(i => (i * 2654435761u) % 10007).ToArray();
        long a = data.Where(x => x % 3 == 0).Select(x => (long)x * 2).Sum();
        long b = data.GroupBy(x => x % 97).Select(g => g.Count() * (long)g.Key).Max();
        long c = data.OrderBy(x => x).Skip(1000).Take(5000).Aggregate(0L, (s, x) => s + x);
        long d = data.Distinct().Count() + data.Select(x => x.ToString()).Where(s => s.Length == 3).Count();
        return a + b + c + d;
    }

    public static long NBody()
    {
        var bodies = new Body[64];
        s_seed = 7;
        for (int i = 0; i < bodies.Length; i++)
            bodies[i] = new Body { Position = new Vec3(Next() % 1000, Next() % 1000, Next() % 1000), Velocity = new Vec3(0, 0, 0), Mass = 1 + Next() % 10 };
        const double dt = 0.01;
        for (int step = 0; step < 150; step++)
        {
            for (int i = 0; i < bodies.Length; i++)
            {
                var bi = bodies[i];
                var acc = new Vec3(0, 0, 0);
                for (int j = 0; j < bodies.Length; j++)
                {
                    if (i == j) continue;
                    var d = bodies[j].Position - bi.Position;
                    double r2 = d.LengthSquared + 0.01;
                    acc = acc + d * (bodies[j].Mass / (r2 * Math.Sqrt(r2)));
                }
                bi.Velocity = bi.Velocity + acc * dt;
            }
            for (int i = 0; i < bodies.Length; i++) bodies[i].Position = bodies[i].Position + bodies[i].Velocity * dt;
        }
        double e = 0;
        foreach (var b in bodies) e += b.Position.X + b.Position.Y + b.Position.Z + b.Velocity.LengthSquared;
        return (long)(e * 1000);
    }

    public static long Shapes()
    {
        s_seed = 8;
        var shapes = new Shape[4096];
        for (int i = 0; i < shapes.Length; i++)
            shapes[i] = (Next() % 3) switch { 0 => new Circle(1 + Next() % 5), 1 => new Rect(1 + Next() % 5, 1 + Next() % 7), _ => new Tri(2 + Next() % 4, 3) };
        double area = 0; long sides = 0;
        for (int round = 0; round < 300; round++)
            for (int i = 0; i < shapes.Length; i++) { area += shapes[i].Area(); sides += shapes[i].Sides; }
        return (long)area + sides;
    }

    public static long LinkedNodes()
    {
        s_seed = 9;
        ListNode? head = null;
        for (int i = 0; i < 100000; i++) head = new ListNode { Next = head, Name = s_words[Next() % (uint)s_words.Length], Value = i };
        long sum = 0;
        for (int round = 0; round < 20; round++)
            for (var n = head; n != null; n = n.Next) { sum += n.Value; if (n.Name.Length == 3) sum++; }
        // reverse in place
        ListNode? prev = null;
        for (var n = head; n != null;) { var next = n.Next; n.Next = prev; prev = n; n = next; }
        for (var n = prev; n != null; n = n.Next) sum ^= n.Value;
        return sum;
    }
}
