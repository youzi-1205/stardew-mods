namespace AutoWalk;

/// <summary>How a world-map destination click is handled.</summary>
internal enum NavMode
{
    /// <summary>Walk there step by step, crossing maps via warps (the original behaviour).</summary>
    Pathfind,

    /// <summary>Warp straight to the destination tile.</summary>
    Teleport,

    /// <summary>Ignore the click — the map is view-only.</summary>
    Off
}

internal sealed class ModConfig
{
    // How a destination click on the world map is handled: "Pathfind" (walk there), "Teleport"
    // (warp straight there), or "Off" (view-only). Stored as text so it round-trips through both
    // config.json and GMCM's text option; parsed via Enum.TryParse on read.
    public string Mode { get; set; } = "Pathfind";

    // Key that opens the world map for choosing a destination. Any SButton name (e.g. "M").
    public string OpenMapKey { get; set; } = "M";

    // Stop auto-walking when the player clicks the mouse.
    public bool StopOnMouseClick { get; set; } = true;

    // Stop auto-walking when the player presses a movement key (so manual control resumes).
    public bool StopOnMovementKey { get; set; } = true;

    // Force the player to run (not walk) while auto-pathing.
    public bool RunWhilePathing { get; set; } = true;
}
