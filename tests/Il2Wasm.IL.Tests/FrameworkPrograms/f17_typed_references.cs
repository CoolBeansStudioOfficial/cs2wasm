// TypedReference: __makeref (mkrefany), __refvalue (refanyval, writes through it, the wrong type's error) and
// __reftype (refanytype).
using System;

struct V { public int A; public string S; }

static class Program
{
    static void Bump(TypedReference tr) => __refvalue(tr, int) += 10;
    static void Main()
    {
        int x = 5;
        var tr = __makeref(x);
        Bump(tr);
        Console.WriteLine(x + " " + __refvalue(tr, int) + " " + (__reftype(tr) == typeof(int)) + " " + __reftype(tr).Name);
        var v = new V { A = 1, S = "s" };
        var tv = __makeref(v);
        __refvalue(tv, V).S += "!";
        Console.WriteLine(v.S + " " + __reftype(tv).Name);
        string str = "abc";
        var ts = __makeref(str);
        Console.WriteLine(__refvalue(ts, string).Length + " " + (__reftype(ts) == typeof(string)));
        try { Console.WriteLine(__refvalue(tr, long)); } catch (InvalidCastException e) { Console.WriteLine("cast: " + e.Message); }
    }
}
