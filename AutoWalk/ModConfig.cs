namespace AutoWalk;

internal sealed class ModConfig
{
    // Key that opens the world map for choosing a destination. Any SButton name (e.g. "M").
    public string OpenMapKey { get; set; } = "M";

    // Stop auto-walking when the player clicks the mouse.
    public bool StopOnMouseClick { get; set; } = true;

    // Stop auto-walking when the player presses a movement key (so manual control resumes).
    public bool StopOnMovementKey { get; set; } = true;

    // Force the player to run (not walk) while auto-pathing.
    public bool RunWhilePathing { get; set; } = true;
}
