// Boxes of structs that hold references, freed many times over: a struct enumerator through its interface, a
// KeyValuePair as object, a struct with a nested struct of references.
using System;
using System.Collections.Generic;

struct Inner { public string A; public object B; }
struct Outer { public Inner I; public string C; public int N; public override string ToString() => I.A + I.B + C + N; }

static class Program
{
    static void Main()
    {
        var list = new List<string> { "a", "b", "c" };
        long total = 0;
        for (int round = 0; round < 2000; round++)
        {
            IEnumerable<string> e = list;
            foreach (var s in e) total += s.Length;
            object kv = new KeyValuePair<string, string>("k" + round, "v");
            total += kv.ToString()!.Length;
            object o = new Outer { I = new Inner { A = "x" + round, B = new object() }, C = "y", N = round };
            total += o.ToString()!.Length;
            var back = (Outer)o;
            total += back.I.A.Length;
        }
        Console.WriteLine(total);
        Console.WriteLine(new Outer { I = new Inner { A = "p", B = 5 }, C = "q", N = 1 });
    }
}
