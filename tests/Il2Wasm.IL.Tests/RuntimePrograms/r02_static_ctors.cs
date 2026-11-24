// Static constructors (SPEC 3): what triggers one in a type without beforefieldinit (a static field, a static method,
// a constructor, a base class's constructor called by a derived one, a value type's instance method), once each;
// one that uses itself again while it runs sees its statics as they are so far; one that throws becomes a
// TypeInitializationException, thrown again (the same object) at every later access.
using System;

static class Log { public static string Text = ""; public static void Add(string s) { Text += s + ";"; } }

class Plugin { static Plugin() => Log.Add("Plugin"); public Plugin() { } }
class StrictBase { static StrictBase() => Log.Add("StrictBase"); public StrictBase() { } }
class LazyDerived : StrictBase { public LazyDerived() { } }
class Generic<T> { static Generic() => Log.Add("Generic " + typeof(T).Name); public Generic() { } }
class StaticField { public static int Value = 1; static StaticField() => Log.Add("StaticField"); }
class StaticMethod { static StaticMethod() => Log.Add("StaticMethod"); public static void Nothing() { } }
struct ValueMethod
{
    static int s_calls;
    static ValueMethod() => Log.Add("ValueMethod");
    public int Get() => ++s_calls;
}

class Recursive
{
    public static int A = 5;
    public static int B;
    static Recursive() { Log.Add("Recursive starts, A " + A + " B " + B); B = Peek() + 1; Log.Add("Recursive ends"); }
    static int Peek() => A + B;
}

class Failing
{
    public static int Value = 3;
    static Failing() { Log.Add("Failing runs"); throw new InvalidOperationException("broken"); }
    public static void Touch() { }
}

namespace Outer
{
    class Holder
    {
        public class Nested
        {
            public static int V;
            static Nested() { throw new ArgumentException("nested broke"); }
        }
    }
}

static class Program
{
    static void Main()
    {
        new Plugin(); new Plugin();
        Console.WriteLine(Log.Text); Log.Text = "";
        new LazyDerived();
        Console.WriteLine(Log.Text); Log.Text = "";
        new Generic<int>(); new Generic<string>(); new Generic<int>();
        Console.WriteLine(Log.Text); Log.Text = "";
        Console.WriteLine(StaticField.Value + " " + Log.Text); Log.Text = "";
        StaticMethod.Nothing(); StaticMethod.Nothing();
        Console.WriteLine(Log.Text); Log.Text = "";
        var vm = new ValueMethod();
        Console.WriteLine("before value method: " + Log.Text);
        Console.WriteLine(vm.Get() + " " + vm.Get() + " " + Log.Text); Log.Text = "";
        Console.WriteLine(Recursive.B + " " + Log.Text); Log.Text = "";

        TypeInitializationException first = null;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (i == 1) Failing.Touch();
                else Console.WriteLine(Failing.Value);
            }
            catch (TypeInitializationException e)
            {
                Console.WriteLine(e.Message + " | " + e.TypeName + " | " + e.InnerException?.GetType().Name + ": " + e.InnerException?.Message);
                Console.WriteLine("same object as the first: " + (first == null ? "first" : ReferenceEquals(first, e).ToString()));
                first ??= e;
            }
        }
        Console.WriteLine(Log.Text); Log.Text = "";
        try { Console.WriteLine(Outer.Holder.Nested.V); }
        catch (Exception e) { Console.WriteLine(e.GetType().Name + ": " + e.Message + " | " + e.InnerException?.Message); }
    }
}
