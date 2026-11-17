using Godot;

// Proof 5: hello2d's HUD (samples/hello2d/src/hud.rs). Shows the character's state: it reads another script's
// instance through the node it is on.
public partial class Hud : Label
{
    [Export] public string CharacterPath = "../../Character";
    [Export] public bool ShowPosition = true;

    public override void _Process(double delta)
    {
        var character = GetNodeOrNull<Character>(CharacterPath);
        if (character == null)
        {
            Text = "no character";
            return;
        }
        string text = $"Arrows/A-D move, Space jumps, P panics\njumps: {character.Jumps}";
        if (ShowPosition)
        {
            Vector2 p = character.Position;
            text += $"\nposition: ({p.X:F0}, {p.Y:F0})";
        }
        Text = text;
    }
}
