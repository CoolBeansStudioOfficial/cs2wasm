// A count's other path taken in the middle of a loop: loops that store references into arrays, lists and fields,
// where the stored value is a literal (never freed: its count is not changed in place), a member of a group (objects
// that can refer to each other), or null, and where the value written over had its last reference there (it ends,
// and what it refers to) or was a literal. After the store the turn goes on in every way a turn can: more
// statements, a second and a third store, a value chosen by ?: or ??, a break, a continue, a goto past the code after
// the loop, a switch, a return out of the loop, an exception caught outside it. Every loop's arrays are printed after
// it. Wasm/ColdExits.cs (CS2WASM_COLD_EXITS=1: the call such a path makes written after the loop, with what was left
// of the turn, and the loop entered again) must leave every line as it is, and with one of its rules taken out lines
// differ or the module is not valid: tools/cold-exits-proof.sh. No loop here calls anything else (a remainder is a
// call), so that each is one the pass takes.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

sealed class Item { public int Id; public string Tag = ""; public Item? Other; }
sealed class Node { public int Id; public Node? Next; public string Name = ""; }
sealed class Holder { public string? S; public Item? I; public Node? N; public int Sets; }

static class Program
{
    static uint s_seed = 12345;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }
    static readonly string[] s_words = { "ore", "wood", "stone", "iron", "gold", "gem", "fish", "meat" };

    static string Made(int i) => new StringBuilder().Append('s').Append(i).ToString();
    static string Show(string?[] a)
    {
        var sb = new StringBuilder();
        foreach (var s in a) sb.Append(s ?? "-").Append(' ');
        return sb.ToString();
    }
    static string Show(Item?[] a)
    {
        var sb = new StringBuilder();
        foreach (var x in a) { if (x == null) sb.Append("- "); else sb.Append(x.Id).Append(':').Append(x.Tag).Append(x.Other != null ? "+" + x.Other.Id : "").Append(' '); }
        return sb.ToString();
    }
    static string Show(Node?[] a)
    {
        var sb = new StringBuilder();
        foreach (var x in a) { if (x == null) sb.Append("- "); else sb.Append(x.Id).Append(x.Name).Append(x.Next != null ? ">" + x.Next.Id : "").Append(' '); }
        return sb.ToString();
    }
    static Item[] Items(int n, int from = 0)
    {
        var a = new Item[n];
        for (int i = 0; i < n; i++) a[i] = new Item { Id = from + i, Tag = (i % 3 == 0) ? s_words[i & 7] : Made(from + i) };
        return a;
    }
    // (a ring of nodes: members of one group)
    static Node[] Ring(int n, int from)
    {
        var a = new Node[n];
        for (int i = 0; i < n; i++) a[i] = new Node { Id = from + i, Name = (i & 1) == 0 ? "n" : Made(i) };
        for (int i = 0; i < n; i++) a[i].Next = a[(i + 1) % n];
        return a;
    }

    // the survivors moved down, the dead written over: each one written over ends there, with its string
    [MethodImpl(MethodImplOptions.NoInlining)] static int Compact(Item?[] a, int mod)
    {
        int w = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var p = a[i];
            if (p == null) continue;
            p.Id += 1000;
            if (p.Id % mod != 0) { a[w++] = p; p.Tag += "."; }
        }
        for (int i = w; i < a.Length; i++) a[i] = null;
        return w;
    }
    // literals, made strings and nulls copied over made strings, literals and nulls
    [MethodImpl(MethodImplOptions.NoInlining)] static long Mix(string?[] src, string?[] dst)
    {
        long sum = 0;
        int j = 0;
        for (int i = 0; i < src.Length; i++)
        {
            var s = src[i];
            dst[j] = s;
            j += 7; if (j >= dst.Length) j -= dst.Length;
            if (s != null) sum += s.Length * (i + 1);
        }
        return sum;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Break(string?[] src, string?[] dst, out int after)
    {
        int n = 0;
        after = 0;
        for (int i = 0; i < src.Length; i++)
        {
            dst[i] = src[i];
            if (src[i] != null && src[i]!.Length == 4) break;
            n += 2;
            after = i;
        }
        return n;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int GotoPast(string?[] src, string?[] dst, int k)
    {
        int r = -1;
        for (int i = 0; i < src.Length; i++)
        {
            dst[dst.Length - 1 - i] = src[i];
            if (i == k) goto done;
            r = i;
        }
        r += 1000;
    done:
        return r;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Switch(string?[] src, string?[] dst, Item?[] items)
    {
        int n = 0, c = 4, j = 0;
        for (int i = 0; i < src.Length; i++)
        {
            if (++c == 5) c = 0;
            switch (c)
            {
                case 0: dst[i] = src[i]; n += 1; break;
                case 1: continue;
                case 2: dst[i] = "two"; n += 10; break;
                case 3: items[j] = items[j + 1]; j += 2; n += 100; break;
                default: dst[i] = null; n += 1000; break;
            }
            n += 100000;
        }
        return n;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Chosen(string?[] src, string?[] dst, string made)
    {
        int n = 0;
        for (int i = 0; i < dst.Length; i++)
        {
            dst[i] = (i & 1) == 0 ? src[i] ?? made : ((i & 7) == 3 ? "third" : made);
            n += dst[i]!.Length;
        }
        return n;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Find(Item?[] a, Item?[] b, int id)
    {
        for (int i = 0; i < a.Length; i++)
        {
            b[i] = a[i];
            if (a[i] != null && a[i]!.Id == id) return i;
        }
        return -1;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Throws(Item?[] a, Item?[] b)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i++)
        {
            b[i] = a[i];      // b is shorter: the store throws at its end, after the turns before it
            n += a[i]!.Id;    // and a null element throws here, after its store
        }
        return n;
    }
    // members of groups stored over members of other groups, over plain objects' places and over null
    [MethodImpl(MethodImplOptions.NoInlining)] static int Members(Node?[] src, Node?[] dst)
    {
        int n = 0, j = 0;
        for (int i = 0; i < dst.Length; i++)
        {
            var x = src[j];
            j += 3; if (j >= src.Length) j -= src.Length;
            dst[i] = x;
            if (x != null) n += x.Id;
        }
        return n;
    }
    // two and three stores a turn, into arrays and fields
    [MethodImpl(MethodImplOptions.NoInlining)] static int Two(Item?[] a, Item?[] b, string?[] s, Holder h)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var x = a[i];
            b[a.Length - 1 - i] = x;
            s[i] = x?.Tag;
            n += i;
        }
        for (int i = 0; i < a.Length; i++)
        {
            h.I = a[i];
            h.S = s[i];
            a[i] = b[i];
            h.Sets++;
        }
        return n;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int ListSet(List<string?> list, string?[] src)
    {
        int n = 0, j = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var old = list[i];
            int len = old != null ? old.Length : 0;
            list[i] = src[j];
            if (++j == src.Length) j = 0;
            n += len;
        }
        return n;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int Fields(Holder[] hs, Item?[] items, Node?[] nodes)
    {
        int n = 0, j = 0, k = 0;
        for (int i = 0; i < hs.Length; i++)
        {
            var h = hs[i];
            h.I = items[j];
            h.N = nodes[k];
            if (++j == items.Length) j = 0;
            if (++k == nodes.Length) k = 0;
            h.Sets += i;
            n += h.Sets;
        }
        for (int i = 0; i < hs.Length; i++)
        {
            var h = hs[i];
            h.S = (i & 3) == 0 ? "lit" : h.I?.Tag;
        }
        return n;
    }
    // a do-while and a while with the store first and last
    [MethodImpl(MethodImplOptions.NoInlining)] static int Shapes(string?[] src, string?[] dst)
    {
        int i = 0, n = 0;
        do { dst[i] = src[src.Length - 1 - i]; n += i; i++; } while (i < dst.Length / 2);
        while (i < dst.Length) { n += 3; if ((i & 1) == 0) { i++; continue; } dst[i] = src[i]; i++; }
        return n;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        // 1
        var items = Items(24);
        items[5] = null; items[11] = null;
        int kept = Compact(items, 3);
        Console.WriteLine("compact " + kept + ": " + Show(items));
        kept = Compact(items, 2);
        Console.WriteLine("compact " + kept + ": " + Show(items));
        kept = Compact(items, 1);
        Console.WriteLine("compact " + kept + ": " + Show(items));

        // 2
        var src = new string?[20];
        for (int i = 0; i < src.Length; i++) src[i] = i % 4 == 0 ? s_words[i & 7] : i % 4 == 1 ? Made(i) : i % 4 == 2 ? null : "lit" ;
        var dst = new string?[13];
        for (int i = 0; i < dst.Length; i++) dst[i] = i % 3 == 0 ? Made(100 + i) : i % 3 == 1 ? "old" : null;
        Console.WriteLine("mix " + Mix(src, dst) + ": " + Show(dst));
        Console.WriteLine("mix " + Mix(src, dst) + ": " + Show(dst));

        // 3
        var s2 = new string?[] { Made(1), "a", Made(2), "stop", Made(3), "b" };
        var d2 = new string?[] { Made(11), Made(12), "x", Made(14), Made(15), "y" };
        Console.WriteLine("break " + Break(s2, d2, out int after) + " " + after + ": " + Show(d2));
        s2[3] = Made(4);
        Console.WriteLine("break " + Break(s2, d2, out after) + " " + after + ": " + Show(d2));

        // 4
        for (int k = 0; k < 8; k += 3)
        {
            var d3 = new string?[] { Made(21), "p", Made(23), Made(24), "q", Made(26) };
            Console.WriteLine("goto " + GotoPast(s2, d3, k) + ": " + Show(d3));
        }

        // 5
        var d4 = new string?[22];
        for (int i = 0; i < d4.Length; i++) d4[i] = (i & 1) == 0 ? Made(30 + i) : "z";
        var it4 = Items(9, 40);
        Console.WriteLine("switch " + Switch(src, d4, it4) + ": " + Show(d4) + "| " + Show(it4));

        // 6
        var d5 = new string?[11];
        for (int i = 0; i < d5.Length; i++) d5[i] = Made(50 + i);
        Console.WriteLine("chosen " + Chosen(src, d5, Made(77)) + ": " + Show(d5));

        // 7
        var a6 = Items(9, 60); a6[2] = null;
        var b6 = Items(9, 70);
        Console.WriteLine("find " + Find(a6, b6, 65) + ": " + Show(b6));
        Console.WriteLine("find " + Find(a6, b6, 99) + ": " + Show(b6));

        // 8
        var a7 = Items(8, 80); a7[5] = null;
        var b7 = Items(8, 90);
        try { Console.WriteLine("throws " + Throws(a7, b7)); } catch (NullReferenceException) { Console.WriteLine("null at 5: " + Show(b7)); }
        var b8 = Items(3, 95);
        try { Console.WriteLine("throws " + Throws(a7, b8)); } catch (IndexOutOfRangeException) { Console.WriteLine("past the end: " + Show(b8)); }

        // 9
        var ring = Ring(5, 200);
        var ring2 = Ring(3, 300);
        var nodes = new Node?[12];
        for (int i = 0; i < nodes.Length; i++) nodes[i] = i % 4 == 0 ? null : i % 4 == 1 ? ring2[i % 3] : new Node { Id = 400 + i, Name = Made(i) };
        Console.WriteLine("members " + Members(ring, nodes) + ": " + Show(nodes));
        ring[1] = null!; ring[3] = new Node { Id = 500, Name = "plain" };
        Console.WriteLine("members " + Members(ring, nodes) + ": " + Show(nodes));
        Console.WriteLine("members " + Members(ring2, nodes) + ": " + Show(nodes));
        var lone = new Node?[4];
        Console.WriteLine("members " + Members(lone, nodes) + ": " + Show(nodes));

        // 10
        var a9 = Items(10, 600); a9[4] = null; a9[3]!.Other = a9[7];
        var b9 = Items(10, 700);
        var s9 = new string?[10];
        for (int i = 0; i < s9.Length; i++) s9[i] = (i & 1) == 0 ? Made(800 + i) : "w";
        var h = new Holder { S = Made(1), I = new Item { Id = 1 } };
        Console.WriteLine("two " + Two(a9, b9, s9, h) + ": " + Show(a9) + "| " + Show(b9) + "| " + Show(s9) + "| " + h.S + " " + h.I?.Id + " " + h.Sets);

        // 11
        var list = new List<string?>();
        for (int i = 0; i < 15; i++) list.Add(i % 3 == 0 ? Made(900 + i) : i % 3 == 1 ? "l" : null);
        Console.WriteLine("list " + ListSet(list, src) + ": " + Show(list.ToArray()));
        Console.WriteLine("list " + ListSet(list, d5) + ": " + Show(list.ToArray()));

        // 12
        var hs = new Holder[9];
        for (int i = 0; i < hs.Length; i++) hs[i] = new Holder { S = Made(i), I = new Item { Id = -i, Tag = Made(-i) }, N = (i & 1) == 0 ? ring2[i % 3] : new Node { Id = -i } };
        int f1 = Fields(hs, a9, nodes), f2 = Fields(hs, b9, ring2);
        var sb = new StringBuilder();
        foreach (var x in hs) sb.Append(x.S ?? "-").Append('/').Append(x.I?.Id.ToString() ?? "-").Append('/').Append(x.N?.Id.ToString() ?? "-").Append('/').Append(x.Sets).Append(' ');
        Console.WriteLine("fields " + f1 + " " + f2 + ": " + sb);

        // 13
        var d13 = new string?[14];
        for (int i = 0; i < d13.Length; i++) d13[i] = Made(1000 + i);
        Console.WriteLine("shapes " + Shapes(src, d13) + ": " + Show(d13));

        // many turns, most of them taking the other path
        long total = 0;
        var big = Items(600, 2000);
        for (int round = 0; round < 40; round++)
        {
            for (int i = 0; i < 60; i++) big[(int)(Next() % 600)] = (Next() & 3) == 0 ? null! : new Item { Id = (int)(Next() % 5000), Tag = (Next() & 1) == 0 ? "t" : Made(round) };
            total += Compact(big, 2 + round % 5);
            for (int i = 0; i < big.Length; i++) if (big[i] == null && (i & 1) == 0) big[i] = new Item { Id = i + round, Tag = s_words[i & 7] };
        }
        long check = 0;
        foreach (var x in big) if (x != null) check = check * 31 + x.Id + x.Tag.Length;
        Console.WriteLine("many " + total + " " + check);
    }
}
