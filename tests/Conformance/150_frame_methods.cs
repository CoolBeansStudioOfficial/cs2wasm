// The methods of stack frames (section 6.3): StackFrame.GetMethod of the running code's frames and of an
// exception's, Exception.TargetSite, MethodBase.GetCurrentMethod, in methods, constructors, properties, generic
// and nested types.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Shapes
{
    sealed class Circle
    {
        public readonly MethodBase? Made;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public Circle() { Made = new StackFrame(0).GetMethod(); }

        public MethodBase? Area
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get => new StackFrame(0).GetMethod();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public MethodBase? Scale(int by, string why) => new StackFrame(0).GetMethod();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public MethodBase? Scale(double by) => new StackFrame(0).GetMethod();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Fail(int code) => throw new InvalidOperationException("fail " + code);
    }

    static class Holder<T>
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static MethodBase? Here() => new StackFrame(0).GetMethod();
    }
}

static class Program
{
    static string Show(MethodBase? m) => m == null ? "null" : m.Name + " | " + m.DeclaringType + " | " + m + " | " + m.IsStatic + " " + m.IsConstructor + " " + m.GetParameters().Length;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void A()
    {
        var trace = new StackTrace(0, true);
        Console.WriteLine(trace.FrameCount > 0);
        StackFrame frame = trace.GetFrame(0)!;
        Console.WriteLine(frame.GetMethod()?.Name);
        Console.WriteLine(Show(frame.GetMethod()));
        Console.WriteLine(Show(new StackTrace().GetFrame(1)!.GetMethod()));
        B(3);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void B(int n) => Console.WriteLine(Show(new StackFrame(0).GetMethod()) + " / " + Show(new StackFrame(1).GetMethod()));

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Current() => Console.WriteLine(Show(MethodBase.GetCurrentMethod()));

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Spanned(ReadOnlySpan<char> text) => throw new FormatException(text.ToString());

    [MethodImpl(MethodImplOptions.NoInlining)]
    static MethodBase? TwiceFrame(int x) => new StackFrame(0).GetMethod();

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        A();
        var c = new Shapes.Circle();
        Console.WriteLine(Show(c.Made));
        Console.WriteLine(Show(c.Area));
        Console.WriteLine(Show(c.Scale(2, "x")));
        Console.WriteLine(Show(c.Scale(2.5)));
        Console.WriteLine(Show(Shapes.Holder<string>.Here()));
        Console.WriteLine(Show(Shapes.Holder<int>.Here()));
        // a generic definition names its parameters
        Console.WriteLine(typeof(Shapes.Holder<>) + " " + typeof(Dictionary<,>) + " " + typeof(Shapes.Holder<>).FullName + " " + (Shapes.Holder<int>.Here()!.DeclaringType == typeof(Shapes.Holder<>)));
        try { Shapes.Holder<int>.Here()!.Invoke(null, null); } catch (Exception e) { Console.WriteLine(e.GetType().Name); }
        Console.WriteLine(new StackFrame(0).GetMethod() == null);
        Current();
        // a frame's method of the program can be invoked
        Console.WriteLine(((MethodBase)TwiceFrame(1)!.Invoke(null, [7])!).Name);
        Console.WriteLine(Show(MethodBase.GetCurrentMethod()));
        try { Shapes.Circle.Fail(4); }
        catch (Exception e)
        {
            Console.WriteLine(Show(e.TargetSite));
            var st = new StackTrace(e, false);
            for (int i = 0; i < st.FrameCount; i++) Console.WriteLine(i + ": " + Show(st.GetFrame(i)!.GetMethod()));
        }
        // (a method that takes a span has a record, without a call: .NET cannot invoke it either)
        try { Spanned("nope"); }
        catch (Exception e) { Console.WriteLine(Show(e.TargetSite)); }
        try { new List<int>()[3] = 1; }
        catch (Exception e) { Console.WriteLine(e.TargetSite?.Name + " " + e.TargetSite?.DeclaringType); }
        Console.WriteLine(new Exception("never thrown").TargetSite == null);
    }
}
