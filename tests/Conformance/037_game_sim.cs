using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

interface IDamageable { int Health { get; } void TakeDamage(int amount, string source); event Action<IDamageable> Died; }

abstract class Entity
{
    static int s_nextId;
    public int Id { get; } = ++s_nextId;
    public string Name { get; init; } = "entity";
    public Vector2 Position;
    public abstract char Glyph { get; }
    public virtual void Update(float dt) { }
    public override string ToString() => $"{Name}#{Id}@{Position}";
}

readonly record struct Vector2(float X, float Y)
{
    public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vector2 operator *(Vector2 a, float s) => new(a.X * s, a.Y * s);
    public float Length => MathF.Sqrt(X * X + Y * Y);
    public override string ToString() => $"({X:0.0}, {Y:0.0})";
}

sealed class Monster : Entity, IDamageable
{
    public int Health { get; private set; }
    public Vector2 Velocity;
    public event Action<IDamageable> Died;
    readonly List<string> _log = new();
    public Monster(int hp) { Health = hp; }
    public override char Glyph => Health > 5 ? 'M' : 'm';
    public override void Update(float dt) => Position += Velocity * dt;
    public void TakeDamage(int amount, string source)
    {
        if (Health <= 0) return;
        Health = Math.Max(0, Health - amount);
        _log.Add($"{source}:{amount}");
        if (Health == 0) Died?.Invoke(this);
    }
    public IReadOnlyList<string> Log => _log;
}

sealed class Chest : Entity
{
    public Dictionary<string, int> Loot { get; } = new() { ["gold"] = 10, ["gem"] = 1 };
    public override char Glyph => '$';
}

static class Program
{
    static string Render(IEnumerable<Entity> es, int w, int h)
    {
        var grid = new char[h, w];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) grid[y, x] = '.';
        foreach (var e in es)
        {
            int x = (int)Math.Round(e.Position.X), y = (int)Math.Round(e.Position.Y);
            if (x >= 0 && x < w && y >= 0 && y < h) grid[y, x] = e.Glyph;
        }
        var sb = new StringBuilder();
        for (int y = 0; y < h; y++) { for (int x = 0; x < w; x++) sb.Append(grid[y, x]); sb.AppendLine(); }
        return sb.ToString();
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var world = new List<Entity>
        {
            new Monster(12) { Name = "orc", Position = new(1, 1), Velocity = new(1, 0.5f) },
            new Monster(4) { Name = "rat", Position = new(6, 2), Velocity = new(-1, 0) },
            new Chest { Name = "chest", Position = new(3, 3) },
        };
        var dead = new List<string>();
        foreach (var d in world.OfType<IDamageable>()) d.Died += x => dead.Add(((Entity)x).Name);
        Console.Write(Render(world, 8, 4));
        for (int tick = 0; tick < 4; tick++)
        {
            foreach (var e in world) e.Update(0.5f);
            foreach (var m in world.OfType<Monster>().Where(m => m.Position.X > 2)) m.TakeDamage(3, "trap" + tick);
        }
        Console.Write(Render(world, 8, 4));
        Console.WriteLine(string.Join("; ", world.Select(e => e.ToString())));
        Console.WriteLine("dead: " + string.Join(",", dead));
        foreach (var m in world.OfType<Monster>()) Console.WriteLine(m.Name + " " + m.Health + " [" + string.Join(" ", m.Log) + "]");
        var chest = world.OfType<Chest>().Single();
        chest.Loot["gold"] += 5;
        chest.Loot.TryAdd("key", 1);
        Console.WriteLine(string.Join(", ", chest.Loot.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
        var byGlyph = world.GroupBy(e => e.Glyph).ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine(string.Join(" ", byGlyph.Select(kv => kv.Key + ":" + kv.Value)));
        var far = world.MaxBy(e => e.Position.Length);
        Console.WriteLine($"farthest {far?.Name} at {far?.Position.Length:F3}");
        var v = new Vector2(3, 4);
        var w2 = v with { Y = 0 };
        Console.WriteLine($"{v} {w2} {v == new Vector2(3, 4)} {v.Length}");
    }
}
