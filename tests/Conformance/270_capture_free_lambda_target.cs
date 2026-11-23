// A capture-free lambda's Delegate.Target (section 1: the same observable output as under .NET): the singleton of
// the class the C# compiler makes for a type's lambdas that capture nothing (`<>c`), the same object for every such
// lambda of the type, its type the delegate's method's declaring type; a static lambda's, a method group's, a local
// function's and a capturing lambda's targets beside it.
#nullable disable
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Other
{
    public Func<int, int> Twice() => x => x * 2;
    public static Func<string> Word() => () => "word";
    public static void Nothing() { }
    int _k = 3;
    public Func<int> Mine() => () => _k;
}

static class Program
{
    static int Plain(int x) => x + 1;

    static string T(object o) => o == null ? "null" : o.GetType().Name + " in " + (o.GetType().DeclaringType?.Name ?? "-");

    static void Show(string what, Delegate d)
    {
        Console.WriteLine(what + ": target " + T(d.Target) + ", method " + d.Method.Name + " static " + d.Method.IsStatic
            + ", same type " + (d.Target != null && d.Target.GetType() == d.Method.DeclaringType));
    }

    static Func<int> Make(int n)
    {
        // no capture, beside one that captures
        Func<int> free = () => 7;
        Func<int> bound = () => n;
        Console.WriteLine("free and bound differ: " + !ReferenceEquals(free.Target, bound.Target));
        Show("bound", bound);
        return free;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Func<int, int> inc = x => x + 1;
        Func<int, int> dec = x => x - 1;
        Action nothing = () => { };
        Show("inc", inc);
        Show("dec", dec);
        Show("nothing", nothing);
        Console.WriteLine("inc target not null: " + (inc.Target != null));
        Console.WriteLine("one singleton: " + ReferenceEquals(inc.Target, dec.Target) + " " + ReferenceEquals(inc.Target, nothing.Target));
        Console.WriteLine("target type: " + inc.Target.GetType().Name + " | " + inc.Target.GetType().FullName + " | " + inc.Target.GetType().DeclaringType);
        Console.WriteLine("target type is the method's: " + (inc.Target.GetType() == inc.Method.DeclaringType) + " " + (dec.Method.DeclaringType == inc.Method.DeclaringType));
        Console.WriteLine("target twice: " + ReferenceEquals(inc.Target, inc.Target));
        Console.WriteLine("target string: " + inc.Target);
        Console.WriteLine("target equals itself: " + inc.Target.Equals(dec.Target) + " hash " + (inc.Target.GetHashCode() == dec.Target.GetHashCode()));

        var made = Make(4);
        Show("made", made);
        Console.WriteLine("made is on the same: " + ReferenceEquals(made.Target, inc.Target));

        // another class's: another singleton
        var o = new Other();
        var twice = o.Twice();
        var word = Other.Word();
        Show("twice", twice);
        Show("word", word);
        Console.WriteLine("other's one singleton: " + ReferenceEquals(twice.Target, word.Target) + ", not Program's: " + !ReferenceEquals(twice.Target, inc.Target));
        Console.WriteLine("other's type: " + twice.Target.GetType().FullName);
        var mine = o.Mine();
        Show("mine", mine);
        Console.WriteLine("mine's target is the object: " + ReferenceEquals(mine.Target, o));

        // a static lambda: on <>c all the same
        Func<int> st = static () => 1;
        Show("static lambda", st);
        Console.WriteLine("static lambda on the singleton: " + ReferenceEquals(st.Target, inc.Target));

        // a method group of a static method: no target
        Func<int, int> plain = Plain;
        Show("plain", plain);
        // of an instance method: the object
        Func<string> ts = o.ToString;
        Console.WriteLine("instance group: " + ReferenceEquals(ts.Target, o) + " " + ts.Method.Name);

        // local functions: a static method without captures (no target), a display class with them
        int Local(int x) => x + 2;
        Func<int, int> lf = Local;
        Show("local", lf);
        int seed = 5;
        int Capturing(int x) => x + seed;
        Func<int, int> lc = Capturing;
        Show("local capturing", lc);
        static int StaticLocal(int x) => x + 3;
        Func<int, int> sl = StaticLocal;
        Show("static local", sl);

        // a function pointer of a static method and of a static local function
        unsafe
        {
            delegate*<int, int> fp = &Plain;
            delegate*<int, int> fl = &StaticLocal;
            Console.WriteLine("function pointers: " + fp(1) + " " + fl(1));
        }

        // lambdas in a loop: one delegate's target every time
        object first = null;
        bool same = true;
        for (int i = 0; i < 3; i++)
        {
            Func<int> f = () => 9;
            if (first == null) first = f.Target; else same &= ReferenceEquals(first, f.Target);
        }
        Console.WriteLine("in a loop: " + same + " " + ReferenceEquals(first, inc.Target));

        // a multicast delegate's target is its last's
        Action a1 = () => { };
        Action both = a1 + o.Mine().Invoke0;
        Console.WriteLine("multicast: " + T(both.Target));
        Action both2 = (Action)Delegate.Combine(new Action(Other.Nothing), a1);
        Console.WriteLine("multicast 2: " + T(both2.Target) + " " + ReferenceEquals(both2.Target, inc.Target));

        // the delegates still run
        Console.WriteLine(inc(1) + " " + dec(1) + " " + made() + " " + twice(4) + " " + word() + " " + mine() + " " + st() + " " + plain(1) + " " + lf(1) + " " + lc(1) + " " + sl(1));

        Console.WriteLine("dynamic invoke: " + inc.DynamicInvoke(1));

        // a lambda in a loop whose body has captured variables of its own, capturing the method's alone: its
        // target is the method's environment, the same every time
        int total = 0;
        object outerEnv = null;
        bool sameEnv = true;
        var keep = new System.Collections.Generic.List<Func<int>>();
        for (int i = 0; i < 3; i++)
        {
            int j = i;
            keep.Add(() => j);
            Action add = () => total++;
            add();
            if (outerEnv == null) outerEnv = add.Target; else sameEnv &= ReferenceEquals(outerEnv, add.Target);
            Console.WriteLine("loop " + i + ": " + T(add.Target) + " " + (add.Target.GetType() == add.Method.DeclaringType)
                + " | " + T(keep[i].Target) + " " + (keep[i].Target.GetType() == keep[i].Method.DeclaringType) + " " + !ReferenceEquals(keep[i].Target, add.Target));
        }
        Console.WriteLine("loop: " + sameEnv + " " + total + " " + !ReferenceEquals(keep[0].Target, keep[1].Target));

        // the singleton is an object like any other
        object s = inc.Target;
        lock (s) Console.WriteLine("locked: " + (s is IDisposable) + " " + (s is object) + " " + (s as string == null));
        var table = new System.Collections.Generic.Dictionary<object, int> { [s] = 1 };
        Console.WriteLine("as a key: " + table[dec.Target]);
        var weak = new WeakReference(s);
        Console.WriteLine("weak: " + ReferenceEquals(weak.Target, s));
    }
}

static class Ext
{
    public static void Invoke0(this Func<int> f) => f();
}
