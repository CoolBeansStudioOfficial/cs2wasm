using Godot;

// Proof 3 (SPEC section 10): a Node2D script whose engine calls are logged under stock Godot 4.7.2 with .NET and
// under the compiled module; the two logs must be equal. Fixed steps, not delta-based, so both runs are the same.
public partial class Mover : Node2D
{
    [Export] public float Speed = 40f;
    private int _frames;

    public override void _Ready()
    {
        Position = new Vector2(10, 20);
        RotationDegrees = 15;
        Scale = new Vector2(2, 2);
        GD.Print("ready at ", Position);
    }

    public override void _Process(double delta)
    {
        Position += new Vector2(Speed * 0.5f, 0);
        Rotation += 0.1f;
        if (++_frames == 5) { GD.Print("done ", Position, " ", Rotation); GetTree().Quit(); }
    }
}
