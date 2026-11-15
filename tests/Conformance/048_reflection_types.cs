using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

class Outer
{
    public class Inner { }
    public class Second { }
    private class Hidden { }
}

class Box<T> { public T Value = default!; }

class Btn
{
    public event Action? Clicked;
    public event EventHandler? Changed;
    public void Click() { Clicked?.Invoke(); Changed?.Invoke(this, EventArgs.Empty); }
}

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        // names the program and CoreLib define
        Console.WriteLine(Type.GetType("System.Int32")?.Name + " " + Type.GetType("System.String")?.FullName + " " + Type.GetType("Outer")?.Name);
        Console.WriteLine((Type.GetType("No.Such.Type") == null) + " " + (Type.GetType("outer", false, true)?.Name ?? "null"));
        try { Type.GetType("No.Such.Type", true); } catch (TypeLoadException) { Console.WriteLine("TypeLoadException"); }

        // generic definitions and instances
        var def = typeof(List<>);
        Console.WriteLine(def.IsGenericType + " " + def.IsGenericTypeDefinition + " " + def.ContainsGenericParameters + " " + def.GetGenericArguments().Length + " " + def.GetGenericArguments()[0].Name);
        var li = typeof(List<int>);
        Console.WriteLine(li.IsGenericTypeDefinition + " " + (li.GetGenericTypeDefinition() == def) + " " + li.GetGenericArguments()[0].Name);
        var made = typeof(Box<>).MakeGenericType(typeof(string));
        Console.WriteLine(made + " " + (made == typeof(Box<string>)) + " " + made.IsConstructedGenericType);
        var dict = typeof(Dictionary<,>).MakeGenericType(typeof(string), typeof(int));
        Console.WriteLine(dict.Name + " " + dict.GetGenericArguments()[1].Name + " " + (dict.GetGenericTypeDefinition() == typeof(Dictionary<,>)));

        // nested types
        Console.WriteLine(typeof(Outer).GetNestedTypes().Length + " " + typeof(Outer).GetNestedType("Second")?.Name + " " + (typeof(Outer).GetNestedType("Hidden") == null));
        foreach (var n in typeof(Outer).GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)) Console.Write(n.Name + " ");
        Console.WriteLine();
        Console.WriteLine(typeof(Outer.Inner).IsNested + " " + typeof(Outer.Inner).DeclaringType!.Name + " " + typeof(Outer.Inner).FullName);

        // events
        var evs = typeof(Btn).GetEvents();
        Console.WriteLine(evs.Length);
        foreach (var e in evs) Console.WriteLine(e.Name + " " + e.EventHandlerType!.Name + " " + e.GetAddMethod()!.Name + " " + e.GetRemoveMethod()!.Name);
        var b = new Btn();
        var clicked = typeof(Btn).GetEvent("Clicked")!;
        Action h = () => Console.WriteLine("clicked");
        clicked.AddEventHandler(b, h);
        typeof(Btn).GetEvent("Changed")!.AddEventHandler(b, new EventHandler((s, a) => Console.WriteLine("changed by " + s!.GetType().Name)));
        b.Click();
        clicked.RemoveEventHandler(b, h);
        b.Click();
        Console.WriteLine(typeof(Btn).GetEvent("Nope") == null);
    }
}
