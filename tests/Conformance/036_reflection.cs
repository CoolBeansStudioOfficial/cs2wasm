using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

[AttributeUsage(AttributeTargets.All)]
sealed class NoteAttribute : Attribute
{
    public string Text { get; }
    public int Level { get; set; }
    public NoteAttribute(string text) { Text = text; }
}

[Note("a player", Level = 2)]
class Player
{
    public string Name = "anon";
    private int _hp = 10;
    public static int Count;
    [Note("health")] public int Health { get => _hp; set => _hp = value; }
    public float Speed { get; private set; } = 1.5f;
    public Player() { Count++; }
    public Player(string name) : this() { Name = name; }
    public int Damage(int amount) { _hp -= amount; return _hp; }
    public static string Describe(Player p, bool loud) => loud ? p.Name.ToUpper() : p.Name;
    public override string ToString() => "Player " + Name;
    private void Secret() { }
    public void Fail() => throw new InvalidOperationException("nope");
}

struct Point { public int X; public int Y; public int Sum() => X + Y; }

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var t = typeof(Player);
        foreach (var m in t.GetMethods()) Console.WriteLine("method " + m);
        foreach (var p in t.GetProperties()) Console.WriteLine("property " + p + " " + p.CanRead + " " + p.CanWrite);
        foreach (var f in t.GetFields()) Console.WriteLine("field " + f + " static=" + f.IsStatic);
        Console.WriteLine(string.Join(",", t.GetFields(BindingFlags.NonPublic | BindingFlags.Instance).Select(f => f.Name)));
        Console.WriteLine(string.Join(",", t.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(m => m.Name)));
        foreach (var c in t.GetConstructors()) Console.WriteLine("ctor " + c + " " + string.Join(",", c.GetParameters().Select(p => p.ToString())));

        var pl = (Player)Activator.CreateInstance(t);
        var named = (Player)Activator.CreateInstance(t, "bob");
        Console.WriteLine(pl + " " + named + " " + Player.Count);
        Console.WriteLine(t.GetMethod("Damage").Invoke(named, new object[] { 3 }));
        t.GetProperty("Health").SetValue(named, 42);
        Console.WriteLine(t.GetProperty("Health").GetValue(named) + " " + t.GetProperty("Speed").GetValue(named));
        t.GetField("Name").SetValue(pl, "zed");
        Console.WriteLine(t.GetField("Name").GetValue(pl) + " " + t.GetField("Count").GetValue(null));
        Console.WriteLine(t.GetMethod("Describe").Invoke(null, new object[] { named, true }));
        try { t.GetMethod("Fail").Invoke(pl, null); }
        catch (TargetInvocationException e) { Console.WriteLine(e.Message + " / " + e.InnerException.Message); }
        try { t.GetMethod("Damage").Invoke(pl, new object[0]); }
        catch (TargetParameterCountException e) { Console.WriteLine(e.Message); }

        var note = t.GetCustomAttribute<NoteAttribute>();
        Console.WriteLine(note.Text + " " + note.Level);
        Console.WriteLine(t.GetProperty("Health").GetCustomAttribute<NoteAttribute>().Text + " " + t.GetProperty("Speed").IsDefined(typeof(NoteAttribute), true));

        var pt = typeof(Point);
        object boxed = new Point { X = 2, Y = 5 };
        Console.WriteLine(pt.GetMethod("Sum").Invoke(boxed, null) + " " + pt.GetField("Y").GetValue(boxed));
        Console.WriteLine(Activator.CreateInstance<Player>().Name + " " + Player.Count);
        Console.WriteLine(t.BaseType + " " + typeof(object).IsAssignableFrom(t) + " " + t.GetMethod("Nope"));
    }
}
