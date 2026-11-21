// A small multiplayer-style game in plain C#: the Invisible API's first slice (docs/FINISH.md 9.4) compiles it with a
// plugin (../Plugin/InvisiblePlugin.cs) and runs it as a server would. Nothing here knows about the plugin: the game
// is compiled the same way with or without it, and .NET runs it without the plugin as the oracle.
//
// The host drives it through exports: `join(id)` adds a player, `input(id, action)` applies one player's input (a
// bit set: 1 right, 2 left, 4 down, 8 up, 16 fire, 32 claim the cell it stands on), `tick()` advances the world one
// tick, `dump()` prints the whole state.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Arena;

/// <summary>One player: position and velocity (set from input, applied each tick), health, a fire cooldown.</summary>
public sealed class Player
{
    public readonly int Id;
    int _x, _y;
    int _vx, _vy;
    int _hp = 10;
    int _cooldown;
    int _shots;

    public Player(int id, int x, int y) { Id = id; _x = x; _y = y; }

    public int X => _x;
    public int Y => _y;
    public bool Alive => _hp > 0;

    public void Steer(int dx, int dy) { _vx = dx; _vy = dy; }

    /// <summary>Fires if the cooldown has run out: the interaction the server gates.</summary>
    public bool TryFire()
    {
        if (_cooldown > 0 || _hp <= 0) return false;
        _cooldown = 3;
        _shots++;
        return true;
    }

    public void Hit(int damage)
    {
        _hp -= damage;
        World.ClampHp(ref _hp);
    }

    /// <summary>Per tick: the velocity rule (moves, kept inside the grid) and the cooldown runs down.</summary>
    public void Step(int width, int height)
    {
        _x = Math.Clamp(_x + _vx, 0, width - 1);
        _y = Math.Clamp(_y + _vy, 0, height - 1);
        if (_cooldown > 0) _cooldown--;
    }

    public string Describe() => "p" + Id + " at " + _x + "," + _y + " v " + _vx + "," + _vy + " hp " + _hp + " cd " + _cooldown + " shots " + _shots;
}

/// <summary>The wave timer: counts down by the tick's length and starts a wave each period. Deterministic, so a server
/// can advance it n ticks at once.</summary>
public sealed class Spawner
{
    int _ms = 1000;
    int _waves;
    int _period = 1300;

    public int Waves => _waves;

    public void Tick(int dtMs)
    {
        _ms -= dtMs;
        if (_ms <= 0)
        {
            _ms += _period;
            _waves++;
        }
    }

    public string Describe() => "spawner " + _ms + " ms, " + _waves + " waves";
}

public sealed class World
{
    public const int Width = 8, Height = 4, TickMs = 50;
    readonly Dictionary<int, Player> _players = new();
    readonly Dictionary<int, int> _coins = new();
    readonly int[] _owner = new int[Width * Height];
    readonly Spawner _spawner = new();
    int _tick;

    public static void ClampHp(ref int hp) { if (hp < 0) hp = 0; }

    public void Join(int id)
    {
        _players[id] = new Player(id, id % Width, id % Height);
        _coins[id] = 0;
    }

    public void Apply(int id, int action)
    {
        if (!_players.TryGetValue(id, out var p) || !p.Alive) return;
        int dx = (action & 1) != 0 ? 1 : (action & 2) != 0 ? -1 : 0;
        int dy = (action & 4) != 0 ? 1 : (action & 8) != 0 ? -1 : 0;
        p.Steer(dx, dy);
        if ((action & 16) != 0 && p.TryFire())
            foreach (var q in _players.Values)
                if (q != p && q.X == p.X) q.Hit(3);
        if ((action & 32) != 0) Claim(id, p);
    }

    void Claim(int id, Player p)
    {
        int cell = p.Y * Width + p.X;
        if (_owner[cell] != 0) return;
        _owner[cell] = id;
        _coins[id] += 1;
    }

    public void Step()
    {
        _tick++;
        foreach (var p in _players.Values) p.Step(Width, Height);
        int before = _spawner.Waves;
        _spawner.Tick(TickMs);
        // a wave pays every player: a deterministic write of the coins, not an interaction
        if (_spawner.Waves != before)
            foreach (var id in _players.Keys) _coins[id] += 2;
    }

    public string Describe()
    {
        var s = "tick " + _tick + "; " + _spawner.Describe();
        var ids = new List<int>(_players.Keys);
        ids.Sort();
        foreach (var id in ids) s += "\n  " + _players[id].Describe() + " coins " + _coins[id];
        s += "\n  owners";
        foreach (var o in _owner) s += " " + o;
        return s;
    }
}

public static class Game
{
    static readonly World s_world = new();

    [UnmanagedCallersOnly(EntryPoint = "join")]
    static void Join(int id) => s_world.Join(id);

    [UnmanagedCallersOnly(EntryPoint = "input")]
    static void Input(int id, int action) => s_world.Apply(id, action);

    [UnmanagedCallersOnly(EntryPoint = "tick")]
    static void Tick() => s_world.Step();

    [UnmanagedCallersOnly(EntryPoint = "dump")]
    static void Dump() => Console.WriteLine(s_world.Describe());
}
