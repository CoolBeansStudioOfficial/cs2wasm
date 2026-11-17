// dynamic: every operator, && and || over a user type with operator true/false, conversions, indexers, compound
// assignment. Expression trees: the node kinds a lambda can produce, compiled and printed.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.InteropServices;

struct Tri
{
    public int V; public Tri(int v) { V = v; }
    public static bool operator true(Tri t) { Console.Write("[true " + t.V + "]"); return t.V > 0; }
    public static bool operator false(Tri t) { Console.Write("[false " + t.V + "]"); return t.V < 0; }
    public static Tri operator &(Tri a, Tri b) => new(Math.Min(a.V, b.V));
    public static Tri operator |(Tri a, Tri b) => new(Math.Max(a.V, b.V));
    public static Tri operator !(Tri a) => new(-a.V);
    public override string ToString() => "Tri" + V;
}
class Money
{
    public decimal Amount; public Money(decimal a) { Amount = a; }
    public static Money operator +(Money a, Money b) => new(a.Amount + b.Amount);
    public static Money operator -(Money a) => new(-a.Amount);
    public static Money operator *(Money a, int k) => new(a.Amount * k);
    public static bool operator ==(Money a, Money b) => a.Amount == b.Amount;
    public static bool operator !=(Money a, Money b) => a.Amount != b.Amount;
    public static bool operator <(Money a, Money b) => a.Amount < b.Amount;
    public static bool operator >(Money a, Money b) => a.Amount > b.Amount;
    public static implicit operator Money(int v) => new(v);
    public static explicit operator int(Money m) => (int)m.Amount;
    public static Money operator ++(Money a) => new(a.Amount + 1);
    public int this[int i] { get => (int)Amount * i; set => Amount = value + i; }
    public override bool Equals(object? o) => o is Money m && m.Amount == Amount;
    public override int GetHashCode() => Amount.GetHashCode();
    public override string ToString() => "$" + Amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
class Node { public int Value; public Node? Next; public List<int> Items = new(); public int[] Arr = { 1, 2, 3 }; public string Name { get; set; } = "n"; }
static class Program
{
    static void T(string name, Func<object?> f)
    {
        try { Console.WriteLine(name + ": " + (f() ?? "null")); } catch (Exception e) { Console.WriteLine(name + " threw " + e.GetType().Name); }
    }
    static string Show<TDel>(Expression<TDel> e) => e.Body.NodeType + " " + e.Body.Type.Name + " | " + e;
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        dynamic a = 7, b = 2, s = "str", d = 2.5, m = new Money(10), big = 5000000000L;
        T("arith", () => (a + b) + " " + (a - b) + " " + (a * b) + " " + (a / b) + " " + (a % b) + " " + (a / d) + " " + (a + big) + " " + (s + a) + " " + (a + s));
        T("bits", () => (a & b) + " " + (a | b) + " " + (a ^ b) + " " + (a << b) + " " + (a >> 1) + " " + (~a) + " " + (-a) + " " + (+a) + " " + (!(a > b)));
        T("compare", () => (a > b) + " " + (a >= 7) + " " + (a < d) + " " + (a == 7.0) + " " + (a != b) + " " + (s == "str") + " " + (a == (object)7));
        T("user ops", () => (m + m) + " " + (-m) + " " + (m * 3) + " " + (m == new Money(10)) + " " + (m < 20) + " " + (m + 5) + " " + (int)m + " " + m[3]);
        T("compound", () => { dynamic x = 5; x += 3; x *= 2; x -= 1; x /= 3; x %= 4; x <<= 2; x |= 1; x ^= 3; x &= 14; x++; --x; return x; });
        T("compound user", () => { dynamic x = new Money(1); x += new Money(2); x++; x[1] = 40; return x; });
        T("string compound", () => { dynamic x = "a"; x += 1; x += 'c'; x += null; return x + "|" + x.Length; });
        T("checked", () => { dynamic x = int.MaxValue; return checked(x + 1); });
        T("unchecked", () => { dynamic x = int.MaxValue; return unchecked(x + 1); });
        T("logic", () => { dynamic t = true, f = false; return (t && f) + " " + (t || f) + " " + (f || t) + " " + (t & f) + " " + (t ^ f) + " " + !t; });
        T("tri and", () => { dynamic x = new Tri(3), y = new Tri(-2); return x && y; });
        T("tri or", () => { dynamic x = new Tri(3), y = new Tri(-2); return y || x; });
        T("tri static left", () => { var x = new Tri(-4); dynamic y = new Tri(9); return x && y; });
        T("tri static left or", () => { var x = new Tri(4); dynamic y = new Tri(9); return x || y; });
        T("tri if", () => { dynamic x = new Tri(1); if (x) return "yes"; return "no"; });
        T("ternary", () => { dynamic x = 3; return x > 2 ? "big" : "small"; });
        T("null ops", () => { dynamic n = null; return (n == null) + " " + (n ?? "dflt") + " " + (n?.Length == null); });
        T("bad op", () => s - 1);
        T("bad call", () => a.Missing());
        T("convert", () => { int i = (int)d; long l = a; double dd = a; string str = s; return i + " " + l + " " + dd + " " + str; });
        T("bad convert", () => { int i = s; return i; });
        T("index", () => { dynamic list = new List<int> { 1, 2, 3 }; dynamic arr = new[] { "x", "y" }; dynamic dict = new Dictionary<string, int> { ["k"] = 9 }; list[1] += 10; dict["k"]++; return list[1] + " " + arr[1] + " " + dict["k"] + " " + list.Count + " " + arr.Length; });
        T("members", () => { dynamic n = new Node { Value = 4 }; n.Value += 6; n.Next = new Node { Value = 1 }; n.Next.Value--; n.Name += "!"; n.Items.Add(n.Value); return n.Value + " " + n.Next.Value + " " + n.Name + " " + n.Items[0] + " " + n.Arr[2]; });
        T("is as", () => { dynamic x = "text"; return (x is string) + " " + (x is int) + " " + ((x as string)?.Length) + " " + (x is string { Length: 4 }); });
        T("switch", () => { dynamic x = 2.5; return x switch { int i => "int " + i, double dv when dv > 2 => "double " + dv, _ => "other" }; });
        T("foreach", () => { dynamic seq = new[] { 1, 2, 3 }; int sum = 0; foreach (var v in seq) sum += v; foreach (int v in seq) sum += v; return sum; });
        T("lambda", () => { dynamic f = (Func<int, int>)(x => x * x); return f(6) + f.Invoke(2); });
        T("overloads", () => Over(a) + " " + Over(s) + " " + Over(d) + " " + Over(m) + " " + Over((dynamic)null!));

        Console.WriteLine(Show<Func<int, int, int>>((x, y) => x * y + (x - y) / 2 % 3));
        Console.WriteLine(Show<Func<int, bool>>(x => x > 1 && x <= 10 || !(x == 5) ^ x != 7));
        Console.WriteLine(Show<Func<int, int>>(x => checked(x + 1) << 2 >> 1 & 0xFF | 8));
        Console.WriteLine(Show<Func<string?, int>>(x => x == null ? -1 : x.Length));
        Console.WriteLine(Show<Func<string?, string>>(x => x ?? "none"));
        Console.WriteLine(Show<Func<int, double>>(x => (double)x / 2 + Math.Sqrt(x)));
        Console.WriteLine(Show<Func<object, bool>>(x => x is string));
        Console.WriteLine(Show<Func<object, string?>>(x => x as string));
        Console.WriteLine(Show<Func<int, int[]>>(x => new[] { x, x + 1 }));
        Console.WriteLine(Show<Func<int, int[]>>(x => new int[x]));
        Console.WriteLine(Show<Func<int[], int>>(x => x[0] + x.Length));
        Console.WriteLine(Show<Func<int, Node>>(x => new Node { Value = x, Name = "made", Items = { x, 2 }, Next = new Node { Value = -x } }));
        Console.WriteLine(Show<Func<int, List<int>>>(x => new List<int> { x, x * 2 }));
        Console.WriteLine(Show<Func<int, Func<int, int>>>(x => y => x + y));
        Console.WriteLine(Show<Func<Node, int>>(n => n.Items[0] + n.Next!.Value + n.Name.Length));
        Console.WriteLine(Show<Func<int, object>>(x => new { A = x, B = "b" }));
        Console.WriteLine(Show<Func<int, string>>(x => x.ToString() + string.Concat("a", "b") + $"{x}!"));
        Console.WriteLine(Show<Func<int?, int>>(x => x.HasValue ? x.Value : x.GetValueOrDefault(3)));
        Console.WriteLine(Show<Func<int?, int?, int?>>((x, y) => x + y * 2));
        Console.WriteLine(Show<Func<Money, Money, Money>>((x, y) => x + -y * 2));
        Console.WriteLine(Show<Func<int, Money>>(x => x));
        Console.WriteLine(Show<Func<IEnumerable<int>, IEnumerable<string>>>(q => q.Where(v => v % 2 == 0).OrderBy(v => -v).Select(v => "#" + v)));
        Console.WriteLine(Show<Func<DayOfWeek, bool>>(dw => dw == DayOfWeek.Friday));
        Console.WriteLine(Show<Func<int, int>>(x => -x + ~x));
        Console.WriteLine(Show<Func<double, double>>(x => Math.Pow(x, 2)));
        Console.WriteLine(Show<Func<Type>>(() => typeof(List<int>)));
        Console.WriteLine(Show<Func<int>>(() => default(int) + sizeof(long)));

        Func<int, int, int> f1 = ((Expression<Func<int, int, int>>)((x, y) => x * y + (x - y) / 2 % 3)).Compile();
        Console.WriteLine(f1(9, 4));
        var mk = ((Expression<Func<int, Node>>)(x => new Node { Value = x, Name = "made", Items = { x, 2 }, Next = new Node { Value = -x } })).Compile()(5);
        Console.WriteLine(mk.Value + " " + mk.Name + " " + mk.Items.Count + " " + mk.Next!.Value);
        Console.WriteLine(((Expression<Func<int, Func<int, int>>>)(x => y => x + y)).Compile()(3)(4));
        Console.WriteLine(((Expression<Func<int?, int?, int?>>)((x, y) => x + y * 2)).Compile()(1, null) == null);
        Console.WriteLine(((Expression<Func<Money, Money, Money>>)((x, y) => x + -y * 2)).Compile()(new Money(10), new Money(3)));
        Console.WriteLine(string.Join(",", ((Expression<Func<IEnumerable<int>, IEnumerable<string>>>)(q => q.Where(v => v % 2 == 0).OrderBy(v => -v).Select(v => "#" + v))).Compile()(Enumerable.Range(1, 6))));
        Console.WriteLine(((Expression<Func<int, object>>)(x => new { A = x, B = "b" })).Compile()(1));
        T("checked tree", () => ((Expression<Func<int, int>>)(x => checked(x + 1))).Compile()(int.MaxValue));

        var px = Expression.Parameter(typeof(int), "x");
        var loopVar = Expression.Variable(typeof(int), "acc");
        var brk = Expression.Label(typeof(int));
        var block = Expression.Block(new[] { loopVar },
            Expression.Assign(loopVar, Expression.Constant(1)),
            Expression.Loop(Expression.IfThenElse(Expression.GreaterThan(px, Expression.Constant(1)),
                Expression.Block(Expression.MultiplyAssign(loopVar, px), Expression.PostDecrementAssign(px)),
                Expression.Break(brk, loopVar)), brk));
        Console.WriteLine(Expression.Lambda<Func<int, int>>(block, px).Compile()(6));
        var sw = Expression.Switch(px, Expression.Constant("many"), Expression.SwitchCase(Expression.Constant("one"), Expression.Constant(1)), Expression.SwitchCase(Expression.Constant("two"), Expression.Constant(2)));
        var swf = Expression.Lambda<Func<int, string>>(sw, px).Compile();
        Console.WriteLine(swf(1) + swf(2) + swf(3));
    }
    static string Over(int x) => "int";
    static string Over(string x) => "string";
    static string Over(double x) => "double";
    static string Over(object x) => "object";
}
