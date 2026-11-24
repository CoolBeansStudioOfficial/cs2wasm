// [UnsafeAccessor]: private constructors, methods (instance, static, virtual, generic), fields and static fields
// of classes and structs, generic owners, and a member that is not there.
using System;
using System.Runtime.CompilerServices;

class Secret
{
    private int _value;
    private static string s_name = "static";
    protected Secret(int v) => _value = v;
    private int Add(int x) => _value + x;
    private static int Twice(int x) => x * 2;
    protected virtual string Who() => "Secret";
    private T Echo<T>(T x) => x;
}
sealed class Sub : Secret { public Sub() : base(1) { } protected override string Who() => "Sub"; }
struct Pt { private int _x; private int Get() => _x; }
class Box<T> { private T _item = default!; private T Item() => _item; }

static class A
{
    [UnsafeAccessor(UnsafeAccessorKind.Constructor)] public static extern Secret Make(int v);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Add")] public static extern int Add(Secret s, int x);
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "Twice")] public static extern int Twice(Secret? s, int x);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Who")] public static extern string Who(Secret s);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Echo")] public static extern T Echo<T>(Secret s, T x);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_value")] public static extern ref int Value(Secret s);
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_name")] public static extern ref string Name(Secret? s);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_x")] public static extern ref int X(ref Pt p);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Get")] public static extern int Get(ref Pt p);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Missing")] public static extern int Missing(Secret s);
}
static class B<T>
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_item")] public static extern ref T Item(Box<T> b);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Item")] public static extern T Read(Box<T> b);
}

static class Program
{
    static void Main()
    {
        var s = A.Make(5);
        Console.WriteLine(A.Add(s, 3) + " " + A.Twice(null, 4) + " " + A.Who(s) + A.Who(new Sub()) + " " + A.Echo(s, "e") + A.Echo(s, 9));
        A.Value(s) = 40;
        A.Name(null) += "!";
        Console.WriteLine(A.Add(s, 2) + " " + A.Name(null));
        var p = new Pt();
        A.X(ref p) = 12;
        Console.WriteLine(A.Get(ref p));
        var b = new Box<string>();
        B<string>.Item(b) = "boxed";
        Console.WriteLine(B<string>.Read(b));
        try { A.Missing(s); } catch (MissingMethodException e) { Console.WriteLine("missing " + e.GetType().Name); }
    }
}
