using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

class P
{
    static void T(string tag, Action a)
    {
        try { a(); }
        catch (Exception e) { Console.WriteLine(tag + ": " + e.GetType() + " " + e.Message + "\n" + e.StackTrace); }
    }

    static int Boom(int x) => throw new InvalidOperationException("boom " + x);

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        T("select", () => new[] { 1, 2 }.Select(Boom).ToList());
        T("where", () => new[] { 1, 2 }.Where(x => Boom(x) > 0).ToArray());
        T("sort", () => { var l = new List<string> { "a", "b", "c" }; l.Sort((x, y) => Boom(1)); });
        T("foreach", () => new List<int> { 1 }.ForEach(x => Boom(x)));
        T("dict", () => { var d = new Dictionary<string, int>(); d.Add("a", 1); d.Add("a", 2); });
        T("parse", () => int.Parse("x"));
        T("list", () => { var l = new List<object>(); _ = l[3]; });
        T("arr", () => Array.ForEach(new[] { 1 }, x => Boom(x)));
        T("first", () => new int[0].First());
        T("lazy", () => new Lazy<int>(() => Boom(2)).Value.ToString());
        T("conv", () => Convert.ToInt32("zz"));
        T("args", () => new string('a', -1));
        T("sub", () => "abc".Substring(5));
        T("any", () => new[] { "a" }.Any(s => Boom(3) > 0));
        T("sum", () => new[] { 1L }.Sum(x => (long)Boom(4)));
        T("max", () => new[] { 1.0 }.Max(x => (double)Boom(5)));
        T("order", () => new[] { 3, 1 }.OrderBy(x => Boom(x)).ToList());
        T("group", () => new[] { "a" }.GroupBy(x => Boom(6)).ToList());
        T("todict", () => new[] { "a" }.ToDictionary(x => x, x => Boom(7)));
        T("count", () => new[] { 'c' }.Count(x => Boom(8) > 0));
        T("all", () => new List<object> { 1 }.All(x => Boom(9) > 0));
        T("find", () => new List<string> { "x" }.Find(x => Boom(10) > 0));
        T("removeall", () => new List<string> { "x" }.RemoveAll(x => Boom(11) > 0));
        T("exists", () => new List<double> { 1 }.Exists(x => Boom(12) > 0));
        T("findidx", () => Array.FindIndex(new[] { "q" }, x => Boom(13) > 0));
        T("trueforall", () => Array.TrueForAll(new object[] { 1 }, x => Boom(14) > 0));
        T("aggregate", () => new[] { 1, 2 }.Aggregate((a, b) => Boom(15)));
        T("selectmany", () => new[] { 1 }.SelectMany(x => new[] { Boom(16) }).ToList());
        T("zip", () => new[] { 1 }.Zip(new[] { 2 }, (a, b) => Boom(17)).ToList());
        T("skipwhile", () => new[] { "s" }.SkipWhile(x => Boom(18) > 0).ToList());
        T("takewhile", () => new[] { 2L }.TakeWhile(x => Boom(19) > 0).ToList());
        T("convertall", () => new List<int> { 1 }.ConvertAll(x => Boom(20)));
        T("arrconvert", () => Array.ConvertAll(new[] { "z" }, x => Boom(21)));
        T("depth", () => Console.WriteLine(new System.Diagnostics.StackTrace().FrameCount > 0));
    }
}
