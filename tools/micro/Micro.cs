using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

sealed class Box { public long V; }
sealed class ListNode { public ListNode? Next; public string Name = ""; public int Value; }
static class P
{
    static uint s_seed;
    static uint Next() { s_seed = s_seed * 1664525u + 1013904223u; return s_seed >> 8; }
    static readonly string[] s_words = "the quick brown fox jumps over the lazy dog while seven wizards quietly judge twelve boxing kangaroos near a frozen river bank".Split(' ');
    static Box[] s_boxes = new Box[16];
    static long[] data = null!;
    static string text = "";
    static string[] parts = null!;
    static List<(string, Func<long>)> s_tests = new();

    static long LongToString() { long sum = 0; for (int i = 0; i < data.Length; i++) { string s = data[i].ToString(); sum += s.Length; } return sum; }
    static long IntToString() { long sum = 0; for (int i = 0; i < 200000; i++) { string s = i.ToString(); sum += s.Length; } return sum; }
    static long Concat2() { long sum = 0; for (int i = 0; i < 200000; i++) { string s = "k" + s_words[i & 15]; sum += s.Length; } return sum; }
    static long Concat4() { long sum = 0; for (int i = 0; i < 200000; i++) { string s = "k" + s_words[i & 15] + ":" + s_words[(i + 3) & 15]; sum += s.Length; } return sum; }
    static long ConcatInt() { long sum = 0; for (int i = 0; i < 20000; i++) { string s = "k" + i + ":" + (i * 3).ToString(); sum += s.Length + s[s.Length - 1]; } return sum; }
    static long NewBoxStore() { long sum = 0; for (int i = 0; i < 200000; i++) { var b = new Box { V = i }; s_boxes[i & 15] = b; sum += b.V; } return sum; }
    static long NewChar8() { long sum = 0; for (int i = 0; i < 200000; i++) { var b = new char[8]; b[i & 7] = (char)i; sum += b.Length + b[3]; } return sum; }
    static long IterSelectWhere() => data.Select(x => x + 1).Where(x => x % 3 == 0).Count();
    static long IterToString() => data.Select(x => x.ToString()).Where(s => s.Length == 3).Count();
    static long GroupBy() => data.GroupBy(x => x % 97).Select(g => g.Count() * (long)g.Key).Max();
    static long Distinct() => data.Distinct().Count();
    static long DelegateCall() { long sum = 0; Func<long, long> f = x => x * 3 + sum; for (int i = 0; i < data.Length; i++) sum += f(data[i]) & 1; return sum; }
    static long IfaceForeach() { long sum = 0; IEnumerable<long> en = data; foreach (var x in en) sum += x; return sum; }
    static long Split() => text.Split(';').Length;
    static long IndexOf() { long sum = 0; foreach (var part in parts) sum += part.IndexOf("fox", StringComparison.Ordinal); return sum; }
    static long SbAppendStr() { var sb = new StringBuilder(); for (int i = 0; i < 100000; i++) sb.Append(s_words[i & 15]); return sb.Length; }
    static long SbAppendChar() { var sb = new StringBuilder(); for (int i = 0; i < 100000; i++) sb.Append(i % 7 == 0 ? ';' : ' '); return sb.Length; }
    static long SbAppendInt() { var sb = new StringBuilder(); for (int i = 0; i < 100000; i++) sb.Append(i); return sb.Length; }
    static long HashSetAdd() { var set = new HashSet<long>(); for (int i = 0; i < data.Length; i++) set.Add(data[i]); return set.Count; }
    static long DictSet() { var d = new Dictionary<int, int>(); for (int i = 0; i < 200000; i++) d[i * 7 % 100000] = i; return d.Count; }
    static Dictionary<int, int> s_d = new();
    static long DictGet() { long sum = 0; var d = s_d; for (int i = 0; i < 200000; i++) if (d.TryGetValue(i, out int v)) sum += v; return sum; }
    static long DictStr() { var ds = new Dictionary<string, int>(); for (int i = 0; i < 200000; i++) { string w = s_words[i % s_words.Length]; ds.TryGetValue(w, out int c); ds[w] = c + 1; } return ds.Count; }
    static long DictStrList()
    {
        s_seed = 2;
        var counts = new Dictionary<string, List<int>>();
        for (int i = 0; i < 120000; i++)
        {
            string w = s_words[Next() % (uint)s_words.Length];
            if (!counts.TryGetValue(w, out var list)) counts[w] = list = new List<int>();
            list.Add(i);
        }
        return counts.Count;
    }
    static long ListAdd() { var l = new List<int>(); for (int i = 0; i < 200000; i++) l.Add(i); return l.Count; }
    static long Upper() => text.ToUpperInvariant().Length;
    static ListNode? BuildList() { ListNode? head = null; for (int i = 0; i < 100000; i++) head = new ListNode { Next = head, Name = s_words[i & 15], Value = i }; return head; }
    static long LlBuildFree() { var h = BuildList(); return h!.Value; }
    static ListNode? s_list;
    static long LlWalk() { long sum = 0; for (var n = s_list; n != null; n = n.Next) { sum += n.Value; if (n.Name.Length == 3) sum++; } return sum; }
    static long LlBuildReverseFree()
    {
        var head = BuildList();
        ListNode? prev = null;
        for (var n = head; n != null;) { var next = n.Next; n.Next = prev; prev = n; n = next; }
        return prev!.Value;
    }
    sealed class TreeNode { public TreeNode? Left, Right; public int Value; }
    static TreeNode BuildTree(int depth, ref int counter)
    {
        var n = new TreeNode { Value = counter++ };
        if (depth > 0) { n.Left = BuildTree(depth - 1, ref counter); n.Right = BuildTree(depth - 1, ref counter); }
        return n;
    }
    static long WalkTree(TreeNode? n) => n == null ? 0 : n.Value + WalkTree(n.Left) + WalkTree(n.Right);
    static TreeNode? s_tree;
    static long TreeBuildFree() { int c = 0; var t = BuildTree(14, ref c); return t.Value + c; }
    static long TreeWalk() => WalkTree(s_tree);
    static long TreeReplace() { int c = 0; var t = s_tree!; t.Left!.Left = BuildTree(10, ref c); return c; }
    static long LlReverse() { ListNode? prev = null; for (var n = s_list; n != null;) { var next = n.Next; n.Next = prev; prev = n; n = next; } s_list = prev; return prev!.Value; }
    // (tools/bench's linked_list, cut after each of its phases: the differences are the phases)
    static long LlBench(int phases)
    {
        s_seed = 9;
        ListNode? head = null;
        for (int i = 0; i < 100000; i++) head = new ListNode { Next = head, Name = s_words[Next() % (uint)s_words.Length], Value = i };
        long sum = 0;
        if (phases < 2) return head!.Value;
        for (int round = 0; round < 20; round++)
            for (var n = head; n != null; n = n.Next) { sum += n.Value; if (n.Name.Length == 3) sum++; }
        if (phases < 3) return sum;
        ListNode? prev = null;
        for (var n = head; n != null;) { var next = n.Next; n.Next = prev; prev = n; n = next; }
        if (phases < 4) return sum + prev!.Value;
        for (var n = prev; n != null; n = n.Next) sum ^= n.Value;
        return sum;
    }
    static long LlB1() => LlBench(1);
    static long LlB2() => LlBench(2);
    static long LlB3() => LlBench(3);
    static long LlB4() => LlBench(4);
    static long TreeB1() { int c = 0; var root = BuildTree(15, ref c); return c; }
    static long TreeB2() { int c = 0; var root = BuildTree(15, ref c); return WalkTree(root); }
    static long TreeB3() { int c = 0; var root = BuildTree(15, ref c); long s = WalkTree(root); root.Left = BuildTree(10, ref c); return s + WalkTree(root); }
    static long Virtual() { long s = 0; object[] os = s_objs; for (int r = 0; r < 20; r++) for (int i = 0; i < os.Length; i++) s += os[i].GetHashCode(); return s; }
    static object[] s_objs = null!;
    sealed class H1 { public override int GetHashCode() => 1; }
    sealed class H2 { public override int GetHashCode() => 2; }
    sealed class H3 { public override int GetHashCode() => 3; }

    public static void Run()
    {
        data = Enumerable.Range(0, 200000).Select(i => (i * 2654435761u) % 10007).ToArray();
        s_seed = 3;
        var sb = new StringBuilder();
        for (int i = 0; i < 40000; i++) { sb.Append(s_words[Next() % (uint)s_words.Length]); sb.Append(i % 7 == 0 ? ';' : ' '); sb.Append(i); sb.Append(' '); }
        text = sb.ToString();
        parts = text.Split(';');
        for (int i = 0; i < 16; i++) s_boxes[i] = new Box();
        for (int i = 0; i < 100000; i++) s_d[i * 7 % 50000] = i;
        s_list = BuildList();
        { int c = 0; s_tree = BuildTree(14, ref c); }
        s_objs = new object[10000];
        for (int i = 0; i < s_objs.Length; i++) s_objs[i] = (Next() % 3) switch { 0 => new H1(), 1 => new H2(), _ => new H3() };
        var tests = new (string, Func<long>)[] {
            ("long_tostring", LongToString), ("int_tostring", IntToString), ("concat2", Concat2), ("concat4", Concat4), ("concat_int", ConcatInt),
            ("new_box_store", NewBoxStore), ("new_char8", NewChar8), ("iter_select_where", IterSelectWhere), ("iter_tostring", IterToString),
            ("groupby", GroupBy), ("distinct", Distinct), ("delegate_call", DelegateCall), ("iface_foreach", IfaceForeach), ("split", Split), ("indexof", IndexOf),
            ("sb_append_str", SbAppendStr), ("sb_append_char", SbAppendChar), ("sb_append_int", SbAppendInt), ("hashset_add", HashSetAdd),
            ("dict_set", DictSet), ("dict_get", DictGet), ("dict_str", DictStr), ("dict_str_list", DictStrList), ("list_add", ListAdd), ("upper", Upper),
            ("ll_build_free", LlBuildFree), ("ll_walk", LlWalk), ("ll_build_rev_free", LlBuildReverseFree), ("ll_reverse", LlReverse), ("llb_build", LlB1), ("llb_walk20", LlB2), ("llb_reverse", LlB3), ("llb_xor", LlB4), ("treeb_build", TreeB1), ("treeb_walk", TreeB2), ("treeb_replace", TreeB3), ("tree_build_free", TreeBuildFree), ("tree_walk", TreeWalk), ("tree_replace", TreeReplace), ("virtual3", Virtual),
        };
        long chk = 0;
        foreach (var (name, f) in tests)
        {
            long best = long.MaxValue;
            for (int r = 0; r < 25; r++)
            {
                long t0 = Stopwatch.GetTimestamp();
                chk += f();
                long us = (Stopwatch.GetTimestamp() - t0) * 1000000 / Stopwatch.Frequency;
                if (us < best) best = us;
            }
            Console.WriteLine("phase " + name + " " + best);
        }
        Console.WriteLine("check " + chk);
    }
}
