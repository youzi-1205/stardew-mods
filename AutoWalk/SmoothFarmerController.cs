using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Pathfinding;

namespace AutoWalk;

/// <summary>A <see cref="PathFindController"/> for the local player.
///
/// Why this class exists: outside of events, Game1.UpdateControlInput runs every tick BEFORE the
/// controller and — since no movement key is physically held — strips the controller-set movement
/// directions and calls player.Halt() (Game1.UpdateControlInput: the setMoving(64) release path and
/// the unconditional no-keys-held branch). Halt -> FarmerSprite.StopAnimation wipes the gait state
/// every tick: currentSingleAnimation = -1, currentAnimationIndex = 0, timer = 0. Later the same
/// tick Farmer.updateMovementAnimation rebuilds the gait via FarmerSprite.animate(id, time), but it
/// restores the already-zeroed index/timer, so the cycle is pinned at index 0 — and frame 0 of every
/// gait cycle is the STANDING frame (run-right starts AnimationFrame(6, 80)). The player slides.
///
/// Fix: each tick this controller pushes exactly one movement direction (smooth, never-empty
/// movement), and the gait PHASE (animation id + frame index + timer) is captured at the END of each
/// tick (see ModEntry calling <see cref="CaptureGaitPhase"/> from UpdateTicked) and re-installed in
/// <see cref="moveCharacter"/> — which runs after the input wipe but before updateMovementAnimation.
/// With the phase surviving across ticks, the vanilla animation machinery accumulates time normally,
/// currentAnimationTick advances the frames, and the walk/run cycle (and footsteps) play natively.
/// All members touched are public — no Harmony, no reflection.</summary>
internal sealed class SmoothFarmerController : PathFindController
{
    private readonly bool run;
    private int savedAnim = -1;
    private int savedIndex;
    private float savedTimer;

    public SmoothFarmerController(Farmer who, GameLocation location, Point endPoint, bool run)
        : base(who, location, endPoint, -1)
    {
        this.run = run;
    }

    protected override void moveCharacter(GameTime time)
    {
        Farmer who = Game1.player;

        if (this.pathToEndPoint == null || this.pathToEndPoint.Count == 0)
            return;

        if (this.run)
            who.setRunning(true, force: true);

        // Pop every node we've already reached.
        while (this.pathToEndPoint.Count > 0 && HasReached(who, this.pathToEndPoint.Peek()))
        {
            this.pathToEndPoint.Pop();
            if (this.pathToEndPoint.Count == 0)
            {
                this.savedAnim = -1; // let the final stop be a clean vanilla stand
                who.Halt();
                if (this.finalFacingDirection != -1)
                    who.faceDirection(this.finalFacingDirection);
                this.endBehaviorFunction?.Invoke(who, this.location);
                return;
            }
        }

        // Always push exactly one direction toward the current node, so movementDirections is
        // never empty while walking. Axis choice has a DEADZONE: once an axis is within a few
        // pixels, commit to the other axis — picking by raw magnitude made the farmer overshoot
        // back and forth (each step is ~5.5px) and twitch in place without ever fixing the
        // remaining 1-2px on the other axis.
        Point node = this.pathToEndPoint.Peek();
        Rectangle box = who.GetBoundingBox();
        int dx = (node.X * 64 + 32) - box.Center.X;
        int dy = (node.Y * 64 + 56) - box.Bottom; // aim the feet a comfortable margin into the tile

        bool moveVertical;
        if (Math.Abs(dx) <= 4)
            moveVertical = true;
        else if (Math.Abs(dy) <= 4)
            moveVertical = false;
        else
            moveVertical = Math.Abs(dy) > Math.Abs(dx);

        who.movementDirections.Clear();
        if (moveVertical)
        {
            if (dy >= 0) who.SetMovingDown(b: true);
            else who.SetMovingUp(b: true);
        }
        else
        {
            if (dx >= 0) who.SetMovingRight(b: true);
            else who.SetMovingLeft(b: true);
        }

        who.canMove = true;
        who.MovePosition(time, Game1.viewport, this.location);

        this.RestoreGaitPhase(who);
    }

    /// <summary>Undo this tick's input-side Halt/StopAnimation wipe by re-installing the gait phase
    /// captured at the end of the previous tick. updateMovementAnimation then sees a matching
    /// currentSingleAnimation, skips its rebuild, and the timer accumulates across ticks so
    /// currentAnimationTick can advance the frames.</summary>
    private void RestoreGaitPhase(Farmer who)
    {
        FarmerSprite sprite = who.FarmerSprite;
        if (this.savedAnim < 0 || sprite.PauseForSingleAnimation || who.UsingTool)
            return;

        sprite.setCurrentFrame(this.savedAnim, this.savedIndex); // rebuilds the gait list at the right index (also sets CurrentFrame + per-frame interval)
        sprite.currentSingleAnimation = this.savedAnim;          // so animate(id, time) takes the no-rebuild path
        sprite.timer = this.savedTimer;                          // restore accumulated time (setCurrentFrame zeroed it)
        sprite.UpdateSourceRect();
    }

    /// <summary>Snapshot the gait phase after the game finished animating this tick. Called from
    /// ModEntry's UpdateTicked handler while this controller is driving the player.</summary>
    public void CaptureGaitPhase(Farmer who)
    {
        FarmerSprite sprite = who.FarmerSprite;
        if (who.movementDirections.Count > 0 && !sprite.PauseForSingleAnimation && !who.UsingTool && sprite.currentSingleAnimation >= 0)
        {
            // Whatever gait the game chose (run/walk/carry variants) — captured as-is.
            this.savedAnim = sprite.currentSingleAnimation;
            this.savedIndex = sprite.currentAnimationIndex;
            this.savedTimer = sprite.timer;
        }
        else
        {
            this.savedAnim = -1;
        }
    }

    private static bool HasReached(Farmer who, Point node)
    {
        // Pixel tolerance instead of strict rectangle containment: each movement step is ~5.5px,
        // so a ±6px window is always crossable — the old containment + bottom-margin combo could
        // be geometrically unsatisfiable next to obstacles, leaving the farmer twitching forever.
        Rectangle box = who.GetBoundingBox();
        int dx = (node.X * 64 + 32) - box.Center.X;
        int dy = (node.Y * 64 + 56) - box.Bottom;
        return Math.Abs(dx) <= 6 && Math.Abs(dy) <= 6;
    }
}
