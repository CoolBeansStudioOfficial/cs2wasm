// ValueType.Equals and ValueType.GetHashCode for a struct with no override of its own: as .NET (CanCompareBits), a value
// with reference, float or overriding fields compares field by field: each reference field by object.Equals (so distinct
// but equal strings are equal), a float by double.Equals (0.0 and -0.0 alike, NaN alike), a field whose type overrides
// Equals by that override, a Nullable by its value; the hash comes from the first field that decides. Before the fix, the references compared by identity, so
// equal values with distinct strings were unequal and a Dictionary keyed by such a struct did not find its entries.
// Nested structs flatten: an outer struct's equality reaches the inner struct's string.
using System;
using System.Collections.Generic;

struct Key { public string Name; public int Id; }
struct Inner { public string Text; public int Count; }
struct Outer { public Inner In; public int Tag; }
struct Nulls { public string S; public object O; }
struct Plain { public int X; public int Y; }
struct Floats { public double D; public float F; }
// overrides Equals and GetHashCode: a field of this type compares by them, not by its bytes
struct Tagged
{
    public int Id;
    public override bool Equals(object obj) => obj is Tagged t && t.Id % 10 == Id % 10;
    public override int GetHashCode() => Id % 10;
}
struct Holder { public Tagged T; public int K; }
struct Nullables { public int? V; public int K; }
struct Bits { public long A; public byte B; public short C; }

static class Program
{
    // a computed string, so each call makes a new object
    static string Make(string prefix, int n) => prefix + n;

    static void Main()
    {
        var a = new Key { Name = Make("a", 1), Id = 3 };
        var b = new Key { Name = Make("a", 1), Id = 3 };
        Console.WriteLine("same string object: " + ReferenceEquals(a.Name, b.Name));
        Console.WriteLine("equal distinct strings: " + a.Equals(b) + " boxed: " + ((object)a).Equals((object)b));
        Console.WriteLine("different int: " + a.Equals(new Key { Name = Make("a", 1), Id = 4 }));
        Console.WriteLine("different string: " + a.Equals(new Key { Name = Make("a", 2), Id = 3 }));
        Console.WriteLine("null string: " + a.Equals(new Key { Name = null, Id = 3 }));
        Console.WriteLine("hash equal: " + (a.GetHashCode() == b.GetHashCode()));
        Console.WriteLine("hash of null first field: " + (new Key { Name = null, Id = 3 }.GetHashCode() == new Key { Name = null, Id = 3 }.GetHashCode()));
        Console.WriteLine("null-first hash differs from string: " + (new Key { Name = null, Id = 3 }.GetHashCode() != a.GetHashCode()));

        var dict = new Dictionary<Key, int>();
        dict[a] = 10;
        Console.WriteLine("dictionary lookup by an equal key: " + dict[b] + ", count " + dict.Count);
        dict[new Key { Name = Make("a", 1), Id = 3 }] = 20;
        Console.WriteLine("dictionary overwrite: " + dict[a] + ", count " + dict.Count);
        Console.WriteLine("dictionary contains a different key: " + dict.ContainsKey(new Key { Name = Make("a", 1), Id = 4 }));

        var o1 = new Outer { In = new Inner { Text = Make("t", 2), Count = 5 }, Tag = 7 };
        var o2 = new Outer { In = new Inner { Text = Make("t", 2), Count = 5 }, Tag = 7 };
        Console.WriteLine("nested equal: " + o1.Equals(o2) + ", hash equal: " + (o1.GetHashCode() == o2.GetHashCode()));
        var o3 = o1;
        o3.In.Count = 6;
        Console.WriteLine("nested differs in an inner int: " + o1.Equals(o3));
        var o4 = new Outer { In = new Inner { Text = Make("u", 2), Count = 5 }, Tag = 7 };
        Console.WriteLine("nested differs in an inner string: " + o1.Equals(o4));
        var outerDict = new Dictionary<Outer, string> { [o1] = "found" };
        Console.WriteLine("nested dictionary lookup: " + outerDict[o2] + ", count " + outerDict.Count);

        var n1 = new Nulls { S = null, O = null };
        var n2 = new Nulls { S = null, O = null };
        Console.WriteLine("null references equal: " + n1.Equals(n2) + ", hash equal: " + (n1.GetHashCode() == n2.GetHashCode()));
        Console.WriteLine("null against a string: " + n1.Equals(new Nulls { S = Make("s", 1), O = null }));
        Console.WriteLine("object field equal by Equals: " + new Nulls { S = null, O = Make("o", 3) }.Equals(new Nulls { S = null, O = Make("o", 3) }));

        var p1 = new Plain { X = 1, Y = 2 };
        Console.WriteLine("plain struct: " + p1.Equals(new Plain { X = 1, Y = 2 }) + " " + p1.Equals(new Plain { X = 1, Y = 3 }) + " " + (p1.GetHashCode() == new Plain { X = 1, Y = 2 }.GetHashCode()));

        // floats compare as double.Equals and hash alike: 0.0 is -0.0, and a NaN is any NaN
        var f1 = new Floats { D = 0.0, F = 1f };
        var f2 = new Floats { D = -0.0, F = 1f };
        var nan1 = new Floats { D = BitConverter.Int64BitsToDouble(0x7FF8000000000001), F = 2f };
        var nan2 = new Floats { D = double.NaN, F = 2f };
        Console.WriteLine("zero signs: " + f1.Equals(f2) + ", hash equal: " + (f1.GetHashCode() == f2.GetHashCode()));
        Console.WriteLine("NaN payloads: " + nan1.Equals(nan2) + ", hash equal: " + (nan1.GetHashCode() == nan2.GetHashCode()));
        Console.WriteLine("float differs: " + f1.Equals(new Floats { D = 0.0, F = 1.5f }));

        // a struct field with its own Equals: compared by it (ids 3 and 13 are equal to it, 4 is not)
        var h1 = new Holder { T = new Tagged { Id = 3 }, K = 5 };
        var h2 = new Holder { T = new Tagged { Id = 13 }, K = 5 };
        var h3 = new Holder { T = new Tagged { Id = 4 }, K = 5 };
        Console.WriteLine("override field: " + h1.Equals(h2) + " " + h1.Equals(h3) + ", hash equal: " + (h1.GetHashCode() == h2.GetHashCode()));

        // a Nullable field: null equals null whatever its bytes, a value by its value
        var nl1 = new Nullables { V = null, K = 1 };
        var nl2 = new Nullables { V = null, K = 1 };
        var nl3 = new Nullables { V = 7, K = 1 };
        var nl4 = new Nullables { V = 7, K = 1 };
        Console.WriteLine("nullable: " + nl1.Equals(nl2) + " " + nl1.Equals(nl3) + " " + nl3.Equals(nl4) + " " + nl3.Equals(new Nullables { V = 8, K = 1 }));

        // the fields compare one by one (the bytes of its padding are not read): a long, a byte and a short
        var bi1 = new Bits { A = 9, B = 2, C = 3 };
        Console.WriteLine("bits: " + bi1.Equals(new Bits { A = 9, B = 2, C = 3 }) + " " + bi1.Equals(new Bits { A = 9, B = 2, C = 4 }));
    }
}
