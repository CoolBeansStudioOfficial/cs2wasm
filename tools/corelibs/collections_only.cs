// tools/corelibs-check.mjs: a program that uses System.Collections' types and no Console (CoreLib and System.Collections).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static int Main()
    {
        var list = new List<int>();
        var set = new HashSet<string>();
        var dict = new Dictionary<string, int>();
        var sorted = new SortedDictionary<int, string>();
        var stack = new Stack<int>();
        var heap = new PriorityQueue<string, int>();
        for (int i = 0; i < 100; i++)
        {
            list.Add(i * 7 % 13);
            string k = "k" + (i % 17);
            if (set.Add(k)) dict[k] = i; else dict[k] += i;
            sorted[i % 11] = k;
            stack.Push(i);
            heap.Enqueue(k, 100 - i);
        }
        list.Sort();
        int sum = 0;
        foreach (var kv in sorted) sum += kv.Key * kv.Value.Length;
        foreach (var kv in dict) sum += kv.Value;
        while (stack.Count > 90) sum += stack.Pop();
        sum += heap.Dequeue().Length + list[50] + new LinkedList<int>(list).Count;
        return sum;
    }
}
