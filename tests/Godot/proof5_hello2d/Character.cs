using System.Collections.Generic;
using System.Diagnostics;
using Godot;

// Proof 5 (SPEC section 10): Bazoodle's hello2d sample (samples/hello2d/src/character.rs) in C# against Godot's own
// C# API. The player's character: runs and jumps with the input actions of the project.
public partial class Character : CharacterBody2D
{
    [Export] public float Speed = 300f;
    [Export] public float JumpVelocity = 650f;
    [Export] public float Gravity = 1500f;
    public int Jumps;
    private string _name;

    /// A plain struct, not a script.
    private struct FallLimits
    {
        public float MaxSpeed;
        public float Clamp(float vy) => vy > MaxSpeed ? MaxSpeed : vy;
    }

    /// Drag is off in this sample.
    private static float AirDrag(float dv) => dv;

    public override void _Ready()
    {
        GetNodeOrNull<Camera2D>("Camera")?.MakeCurrent();
        _name = Name;
        GD.Print($"{_name} ready at {Position}, speed {Speed}");
        ReportClocks();
    }

    public override void _PhysicsProcess(double delta)
    {
        Vector2 velocity = Velocity;
        velocity.X = Input.GetAxis("move_left", "move_right") * Speed;
        if (IsOnFloor())
        {
            if (Input.IsActionJustPressed("jump"))
            {
                velocity.Y = -JumpVelocity;
                Jumps++;
            }
        }
        else
        {
            velocity.Y = Fall(velocity.Y, delta);
        }
        Velocity = velocity;
        MoveAndSlide();

        if (Input.IsActionJustPressed("debug_panic")) Explode();
    }

    /// Real time is the engine's clock; only what is the same on every run is printed.
    private static void ReportClocks()
    {
        var watch = Stopwatch.StartNew();
        ulong x = 0;
        for (ulong i = 0; i < 200_000; i++) x = unchecked(x + i * i);
        bool advanced = watch.Elapsed.Ticks >= 0;
        GD.Print($"clocks: checksum {x % 7}; a year after 2020: {System.DateTime.UtcNow.Year > 2020}; stopwatch ran: {advanced}");
    }

    /// Gravity for one tick.
    private float Fall(float vy, double delta)
    {
        float dv = AirDrag(Gravity * (float)delta);
        var limits = new FallLimits { MaxSpeed = 2000f };
        return limits.Clamp(vy + dv);
    }

    /// A deliberate exception (press P): it must reach the engine's error report and leave the game running.
    private void Explode()
    {
        var checkpoints = new List<uint>();
        int index = Jumps + 3;
        GD.Print($"about to read checkpoint {index} of {checkpoints.Count}");
        _ = checkpoints[index];
    }
}
