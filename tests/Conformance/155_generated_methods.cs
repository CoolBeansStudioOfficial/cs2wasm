// Reflection on the methods the C# compiler makes (section 6.3): lambdas (on the singleton <>c, on a display class,
// on the type itself when they capture only this), local functions (static, by a struct of captures, converted to a
// delegate), nested ones, iterators' and async methods' MoveNext, their names with the compiler's ordinals, their
// declaring types, parameters and attributes, through Delegate.Method, StackFrame.GetMethod, Exception.TargetSite and
// MethodBase.GetCurrentMethod.
#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

sealed class Counter
{
    int _n = 5;
    // captures this alone: a method of Counter
    public Func<int> Getter() => () => _n;
    // captures a parameter and this: a display class with a field for this
    public Func<int, int> Adder(int k) => x => x + k + _n;
    public MethodBase Here()
    {
        MethodBase m = null;
        Action a = () => m = MethodBase.GetCurrentMethod();
        a();
        return m;
    }
}

static class Program
{
    static string Attrs(MemberInfo m) => m == null ? "-" : string.Join(",", m.GetCustomAttributes(false).Select(a => a.GetType().Name).OrderBy(x => x, StringComparer.Ordinal));
    static string Params(MethodBase m) => string.Join(", ", m.GetParameters().Select(p => p.ParameterType + " " + p.Name));
    static string Show(MethodBase m) => m == null ? "null" : m.Name + " | " + m.DeclaringType + " | " + m.DeclaringType?.Name + " | " + m
        + " | static " + m.IsStatic + " public " + m.IsPublic + " private " + m.IsPrivate + " assembly " + m.IsAssembly + " (" + Params(m) + ") [" + Attrs(m) + "] {" + Attrs(m.DeclaringType) + "}"
        + " cg " + m.IsDefined(typeof(CompilerGeneratedAttribute), false) + " " + (m.DeclaringType?.IsDefined(typeof(CompilerGeneratedAttribute), false));

    static string ShowType(Type t) => t == null ? "null" : t + " | " + t.Name + " | " + t.FullName + " | " + t.DeclaringType + " | nested " + t.IsNested + " value " + t.IsValueType + " sealed " + t.IsSealed + " [" + Attrs(t) + "]";

    [MethodImpl(MethodImplOptions.NoInlining)]
    static MethodBase Caller() => new StackFrame(1).GetMethod();

    static IEnumerable<int> Numbers(int n)
    {
        for (int i = 0; i < n; i++) yield return i;
        Console.WriteLine("iterator current: " + Show(MethodBase.GetCurrentMethod()));
        Console.WriteLine("iterator frame: " + Show(new StackFrame(0).GetMethod()));
        Console.WriteLine("iterator caller: " + Show(Caller()));
    }

    static IEnumerable<string> Failing()
    {
        yield return "a";
        throw new FormatException("iterator");
    }

    static async Task<int> RunAsync(int x)
    {
        await Task.Yield();
        Console.WriteLine("async current: " + Show(MethodBase.GetCurrentMethod()));
        Console.WriteLine("async frame: " + Show(new StackFrame(0).GetMethod()));
        return x * 2;
    }

    static async Task FailAsync(string why)
    {
        await Task.Yield();
        throw new InvalidOperationException(why);
    }

    static void Lambdas()
    {
        // captures nothing: on <>c
        Func<int, int> inc = x => x + 1;
        Console.WriteLine("inc: " + Show(inc.Method));
        // captures a local: on the display class of its scope
        int seed = 10;
        Func<int, int> add = x => x + seed;
        Console.WriteLine("add: " + Show(add.Method));
        // a lambda inside a lambda, and one after it: the ordinals of the scope tree
        Func<Func<int>> outer = () => { Func<int> inner = () => 7; return inner; };
        Func<string, int> after = s => s.Length;
        Console.WriteLine("outer: " + Show(outer.Method));
        Console.WriteLine("inner: " + Show(outer().Method));
        Console.WriteLine("after: " + Show(after.Method));
        // a nested scope's captures: a second display class, the inner one linked to the outer
        for (int i = 0; i < 1; i++)
        {
            int j = i + seed;
            Func<int> both = () => i + j + seed;
            Console.WriteLine("both: " + Show(both.Method) + " = " + both());
        }
        // the frame of a lambda, its current method, its exception's target
        Func<MethodBase> frame = () => new StackFrame(0).GetMethod();
        Console.WriteLine("frame: " + Show(frame()));
        Func<MethodBase> current = () => MethodBase.GetCurrentMethod();
        Console.WriteLine("current: " + Show(current()));
        Action thrower = () => throw new ArgumentException("lambda");
        try { thrower(); } catch (Exception e) { Console.WriteLine("target: " + Show(e.TargetSite)); }
        Func<MethodBase> caller = () => Caller();
        Console.WriteLine("caller: " + Show(caller()));
        // an anonymous method (delegate { }) is a lambda too
        Action<int> anon = delegate (int v) { Console.WriteLine("anon " + v); };
        anon(1);
        Console.WriteLine("anon: " + Show(anon.Method));
        Console.WriteLine("closure type: " + ShowType(add.Target?.GetType()) + " " + (add.Target?.GetType() == add.Method.DeclaringType));
    }

    static void LocalFunctions(int p)
    {
        // static: no captures
        static int Twice(int v) => v * 2;
        // captures by a struct passed by reference
        int Plus(int v) => v + p;
        // converted to a delegate: a method of the display class
        int Times(int v) => v * p;
        Func<int, int> times = Times;
        Console.WriteLine("Twice: " + Twice(2) + " " + Show(Here()));
        Console.WriteLine("Plus: " + Plus(2) + " " + Show(PlusFrame()));
        Console.WriteLine("times: " + times(3) + " " + Show(times.Method));
        Func<int, int> twice = Twice;
        Console.WriteLine("twice: " + Show(twice.Method));
        MethodBase Here() => MethodBase.GetCurrentMethod();
        MethodBase PlusFrame() { _ = p; return new StackFrame(0).GetMethod(); }
        // a local function in a local function
        int Outer(int a) { int Inner(int b) => b + a + p; return Inner(1); }
        Console.WriteLine("Outer: " + Outer(1));
        void Fails() => throw new NotSupportedException("local " + p);
        try { Fails(); } catch (Exception e) { Console.WriteLine("local target: " + Show(e.TargetSite)); }
    }

    static void StructClosure(int q)
    {
        // captures by a struct passed by reference, and its frame
        MethodBase Probe(int v) { _ = q + v; return new StackFrame(0).GetMethod(); }
        Console.WriteLine("probe: " + Show(Probe(1)));
        // an async lambda and an iterator local function: their state machines are classes of their own
        Func<Task<MethodBase>> later = async () => { await Task.Yield(); return MethodBase.GetCurrentMethod(); };
        Console.WriteLine("async lambda: " + Show(later().GetAwaiter().GetResult()));
        IEnumerable<MethodBase> Each() { yield return new StackFrame(0).GetMethod(); }
        foreach (var m in Each()) Console.WriteLine("iterator local: " + Show(m));
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Lambdas();
        LocalFunctions(3);
        StructClosure(2);
        var c = new Counter();
        Console.WriteLine("getter: " + Show(c.Getter().Method));
        Console.WriteLine("adder: " + Show(c.Adder(1).Method));
        Console.WriteLine("here: " + Show(c.Here()));
        // iterators
        foreach (var n in Numbers(2)) Console.WriteLine(n);
        var numbers = Numbers(0);
        Console.WriteLine("iterator type: " + ShowType(numbers.GetType()));
        var it = typeof(Program).GetMethod("Numbers", BindingFlags.NonPublic | BindingFlags.Static);
        Console.WriteLine("Numbers: " + Show(it));
        Console.WriteLine("state machine: " + ShowType(it.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType) + " " + (it.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType == numbers.GetType()));
        try { foreach (var s in Failing()) Console.WriteLine(s); } catch (Exception e) { Console.WriteLine("iterator target: " + Show(e.TargetSite)); }
        foreach (var _ in numbers) { }
        // async methods
        Console.WriteLine(RunAsync(4).GetAwaiter().GetResult());
        var ra = typeof(Program).GetMethod("RunAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Console.WriteLine("RunAsync: " + Show(ra));
        Console.WriteLine("async machine: " + ShowType(ra.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType));
        try { FailAsync("no").GetAwaiter().GetResult(); } catch (Exception e) { Console.WriteLine("async target: " + Show(e.TargetSite)); }
        // the frames of an exception through a lambda and a local function
        try { Func<int> f = () => Deep(0); f(); }
        catch (Exception e)
        {
            var st = new StackTrace(e, false);
            for (int i = 0; i < st.FrameCount; i++) Console.WriteLine(i + ": " + st.GetFrame(i).GetMethod()?.Name + " " + st.GetFrame(i).GetMethod()?.DeclaringType);
        }
        static int Deep(int d) => d > 2 ? throw new DivideByZeroException() : Deep(d + 1);
    }
}
