// Interface methods of a struct called through a type parameter's constraint, the interface nested in the generic type
// (Roslyn's AbstractFlowPass<TLocalState>.ILocalState.Clone() returning the struct); foreach over a class or interface
// whose abstract, virtual or interface GetEnumerator returns a struct enumerator (Roslyn's Rope, IArityEnumerable).
using System;
using System.Runtime.InteropServices;

abstract class Pass<TState> where TState : Pass<TState>.IState
{
    internal interface IState { TState Clone(); bool Reachable { get; } }
    protected TState State;
    public string Run() { var s = State.Clone(); return s.Reachable + " " + Describe(s); }
    protected abstract string Describe(TState s);
}

sealed class Flow : Pass<Flow.LocalState>
{
    internal struct LocalState : IState
    {
        internal bool Alive;
        public LocalState Clone() => this;
        public bool Reachable => Alive;
    }
    public Flow() { State = new LocalState { Alive = true }; }
    protected override string Describe(LocalState s) => "alive=" + s.Alive;
}

abstract class Rope
{
    public struct Enumerator
    {
        private readonly string _s; private int _i;
        public Enumerator(string s) { _s = s; _i = -1; }
        public char Current => _s[_i];
        public bool MoveNext() => ++_i < _s.Length;
    }
    public abstract Enumerator GetEnumerator();
    private sealed class Leaf(string s) : Rope { public override Enumerator GetEnumerator() => new(s); }
    public static Rope ForString(string s) => new Leaf(s);
}

interface IArities { Rope.Enumerator GetEnumerator(); int Count { get; } }
sealed class Arities : IArities { public Rope.Enumerator GetEnumerator() => new("xyz"); public int Count => 3; }

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(new Flow().Run());
        foreach (char c in Rope.ForString("abc")) Console.Write(c);
        IArities a = new Arities();
        foreach (char c in a) Console.Write(c);
        Console.WriteLine(" " + a.Count);
    }
}
