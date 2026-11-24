// Activator.CreateInstance<T>() and CreateInstance(Type) (SPEC 6.6, for the types the module keeps): a class's
// parameterless constructor, a struct's (or its zero value), null for a Nullable, a constructor's exception wrapped
// in TargetInvocationException, MissingMethodException for an abstract class, an interface, a class without a
// public parameterless constructor (CreateInstance(Type, nonPublic: true) finds a private one); Lazy<T> and
// ConditionalWeakTable.GetOrCreateValue, which create through it.
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

public abstract class Abstract { public Abstract() { } }
public class PrivateConstructor { public int V = 3; private PrivateConstructor() { } }
public class OnlyArguments { public OnlyArguments(int value) { } }
public class Public { public int Value = 5; public Public() { Console.WriteLine("Public()"); } }
public class OnlyByType { public int Value = 9; }
public struct WithConstructor { public int Value; public WithConstructor() { Value = 7; } }
public struct Plain { public int Value; }
public class Throwing { public Throwing() => throw new InvalidOperationException("class constructor"); }
public struct ThrowingStruct { public int Value; public ThrowingStruct() => throw new InvalidOperationException("struct constructor"); }

static class Program
{
    static string Try(Func<object> f)
    {
        try { var o = f(); return o == null ? "null" : o.GetType().Name; }
        catch (Exception e) { return e.GetType().Name + ": " + e.Message + (e.InnerException != null ? " ---> " + e.InnerException.GetType().Name + ": " + e.InnerException.Message : ""); }
    }

    static T Make<T>() where T : new() => new T();

    static void Main()
    {
        Console.WriteLine(Try(() => Activator.CreateInstance<Public>()));
        Console.WriteLine(Activator.CreateInstance<Public>().Value);
        Console.WriteLine(Activator.CreateInstance<WithConstructor>().Value);
        Console.WriteLine(Activator.CreateInstance<Plain>().Value);
        Console.WriteLine(Activator.CreateInstance<int>());
        Console.WriteLine(Activator.CreateInstance<int?>() == null);
        Console.WriteLine(Make<Public>().Value + Make<WithConstructor>().Value);
        Console.WriteLine(Try(() => Activator.CreateInstance<string>()));
        Console.WriteLine(Try(() => Activator.CreateInstance<Abstract>()));
        Console.WriteLine(Try(() => Activator.CreateInstance<IDisposable>()));
        Console.WriteLine(Try(() => Activator.CreateInstance<PrivateConstructor>()));
        Console.WriteLine(Try(() => Activator.CreateInstance<OnlyArguments>()));
        Console.WriteLine(Try(() => Activator.CreateInstance<Throwing>()));
        Console.WriteLine(Try(() => Activator.CreateInstance<ThrowingStruct>()));

        Console.WriteLine(Try(() => Activator.CreateInstance(typeof(OnlyByType))));
        Console.WriteLine(((OnlyByType)Activator.CreateInstance(typeof(OnlyByType))).Value);
        Console.WriteLine(Try(() => Activator.CreateInstance(typeof(WithConstructor))));
        Console.WriteLine(((WithConstructor)Activator.CreateInstance(typeof(WithConstructor))).Value);
        Console.WriteLine(Try(() => Activator.CreateInstance(typeof(int?))));
        Console.WriteLine(Try(() => Activator.CreateInstance(typeof(Abstract))));
        Console.WriteLine(Try(() => Activator.CreateInstance(typeof(IDisposable))));
        Console.WriteLine(Try(() => Activator.CreateInstance(typeof(PrivateConstructor))));
        Console.WriteLine(Try(() => Activator.CreateInstance(typeof(PrivateConstructor), nonPublic: true)));
        Console.WriteLine(Try(() => Activator.CreateInstance(typeof(OnlyArguments))));
        Console.WriteLine(Try(() => Activator.CreateInstance(typeof(Throwing))));
        Console.WriteLine(Try(() => Activator.CreateInstance(typeof(string))));

        Console.WriteLine(new Lazy<Public>().Value.Value);
        Console.WriteLine(new Lazy<int?>(LazyThreadSafetyMode.None).Value == null);
        Console.WriteLine(Try(() => new Lazy<string>().Value));
        Console.WriteLine(new ConditionalWeakTable<object, OnlyByType>().GetOrCreateValue(new object()).Value);
    }
}
