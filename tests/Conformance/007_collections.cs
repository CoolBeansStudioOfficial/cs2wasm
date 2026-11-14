using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;


static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var list = new List<int> { 5, 3, 8, 1 };
        list.Add(9);
        list.Insert(0, 7);
        list.Remove(3);
        Console.WriteLine(list.Count + " " + list[0] + " " + list[list.Count - 1]);
        list.Sort();
        foreach (var x in list) Console.Write(x + " ");
        Console.WriteLine();
        list.Sort((a, b) => b.CompareTo(a));
        Console.WriteLine(string.Join(",", list));
        Console.WriteLine(list.Contains(8) + " " + list.IndexOf(8) + " " + list.Find(v => v < 5));
        list.RemoveAll(v => v % 2 == 1);
        Console.WriteLine(string.Join(",", list));
        var words = new List<string> { "pear", "apple", "fig" };
        words.Sort();
        Console.WriteLine(string.Join(" ", words));
        var dict = new Dictionary<string, int>();
        dict["one"] = 1; dict["two"] = 2; dict["three"] = 3;
        dict.Add("four", 4);
        dict.Remove("two");
        dict["five"] = 5;
        dict["one"] = 11;
        foreach (var kv in dict) Console.WriteLine(kv.Key + "=" + kv.Value);
        Console.WriteLine(dict.ContainsKey("two") + " " + dict.TryGetValue("three", out var three) + " " + three + " " + dict.Count);
        foreach (var k in dict.Keys) Console.Write(k + ";");
        Console.WriteLine();
        try { var z = dict["missing"]; } catch (KeyNotFoundException e) { Console.WriteLine(e.Message); }
        try { dict.Add("one", 0); } catch (ArgumentException e) { Console.WriteLine(e.Message); }
        var counts = new Dictionary<char, int>();
        foreach (var c in "mississippi") { counts.TryGetValue(c, out int n); counts[c] = n + 1; }
        foreach (var kv in counts) Console.Write(kv.Key + ":" + kv.Value + " ");
        Console.WriteLine();
        var set = new HashSet<int> { 1, 2, 3 };
        Console.WriteLine(set.Add(2) + " " + set.Add(4) + " " + set.Count + " " + set.Contains(3));
        set.Remove(1);
        Console.WriteLine(string.Join(",", set));
        var q = new Queue<string>();
        q.Enqueue("a"); q.Enqueue("b"); q.Enqueue("c");
        Console.WriteLine(q.Dequeue() + q.Peek() + q.Count);
        var st = new Stack<int>();
        st.Push(1); st.Push(2); st.Push(3);
        Console.WriteLine(st.Pop() + " " + st.Peek() + " " + st.Count);
        int[] arr = { 4, 2, 7, 1, 9, 3 };
        Array.Sort(arr);
        Console.WriteLine(string.Join(" ", arr) + " idx " + Array.IndexOf(arr, 7) + " bs " + Array.BinarySearch(arr, 9));
        Array.Reverse(arr);
        Console.WriteLine(string.Join(" ", arr));
        int[] copy = new int[3];
        Array.Copy(arr, 1, copy, 0, 3);
        Console.WriteLine(string.Join(" ", copy));
        var grid = new int[3][];
        for (int i = 0; i < 3; i++) { grid[i] = new int[i + 1]; for (int j = 0; j <= i; j++) grid[i][j] = i * j; }
        Console.WriteLine(grid[2][2] + grid[1][1]);
        var objs = new List<object> { 1, "two", 3.0, null };
        foreach (var o in objs) Console.Write((o?.ToString() ?? "null") + "|");
        Console.WriteLine();
        try { foreach (var x in list) list.Add(x); } catch (InvalidOperationException e) { Console.WriteLine(e.Message); }
        var big = new Dictionary<int, int>();
        for (int i = 0; i < 1000; i++) big[i * 7 % 1000] = i;
        for (int i = 0; i < 500; i++) big.Remove(i * 2);
        long sum = 0; int first = -1;
        foreach (var kv in big) { sum += kv.Key * (long)kv.Value; if (first < 0) first = kv.Key; }
        Console.WriteLine(big.Count + " " + sum + " " + first);
        var lists = new List<List<string>>();
        for (int i = 0; i < 3; i++) lists.Add(new List<string> { "x" + i });
        lists[1].Add("y");
        Console.WriteLine(lists[1].Count + lists[2][0]);
        List<int> fromArr = new List<int>(new[] { 3, 1, 2 });
        fromArr.Reverse();
        Console.WriteLine(string.Join("", fromArr.ToArray()));
    }
}
