using Microsoft.Xna.Framework;
using StardewModdingAPI;
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
    private readonly IReflectedField<bool> ignoreWarpsField;
    private readonly Func<GameLocation, Point, bool>? isTemporarilyBlocked;
    private int savedAnim = -1;
    private int savedIndex;
    private float savedTimer;

    public SmoothFarmerController(Farmer who, GameLocation location, Point endPoint, bool run, IReflectionHelper reflection, Func<GameLocation, Point, bool>? isTemporarilyBlocked = null)
        : base(who, location, isAtEndPoint, -1, null, 60000, Point.Zero)
    {
        // NOTE: the default PathFindController overload caps A* at 10,000 explored nodes, which
        // genuinely fails on long routes across big maps (Forest/Beach) and produced bogus
        // "can't reach" errors. 60,000 covers any vanilla map.
        this.run = run;
        this.isTemporarilyBlocked = isTemporarilyBlocked;
        this.ignoreWarpsField = reflection.GetField<bool>(location, "ignoreWarps");
        this.endPoint = endPoint;
        this.pathToEndPoint = this.FindPath(who.TilePoint, endPoint);
    }

    private Stack<Point>? FindPath(Point startPoint, Point endPoint)
    {
        return this.isTemporarilyBlocked == null
            ? findPath(startPoint, endPoint, isAtEndPoint, this.location, Game1.player, 60000)
            : this.FindPathAvoidingTemporaryBlocks(startPoint, endPoint, 60000);
    }

    private Stack<Point>? FindPathAvoidingTemporaryBlocks(Point startPoint, Point endPoint, int limit)
    {
        var openList = new System.Collections.Generic.PriorityQueue<PathNode, int>();
        var closedList = new HashSet<int>();

        openList.Enqueue(new PathNode(startPoint.X, startPoint.Y, 0, null), Math.Abs(endPoint.X - startPoint.X) + Math.Abs(endPoint.Y - startPoint.Y));
        int width = this.location.map.Layers[0].LayerWidth;
        int height = this.location.map.Layers[0].LayerHeight;
        int searched = 0;

        while (openList.TryDequeue(out PathNode? node, out _))
        {
            if (node.x == endPoint.X && node.y == endPoint.Y)
                return reconstructPath(node);

            closedList.Add(node.id);
            int nextCost = node.g + 1;

            for (int i = 0; i < 4; i++)
            {
                int x = node.x + Directions[i, 0];
                int y = node.y + Directions[i, 1];
                int hash = PathNode.ComputeHash(x, y);
                if (closedList.Contains(hash))
                    continue;

                bool isEnd = x == endPoint.X && y == endPoint.Y;
                if (!isEnd && (x < 0 || y < 0 || x >= width || y >= height))
                {
                    closedList.Add(hash);
                    continue;
                }

                Point tile = new(x, y);
                if (tile != startPoint && this.isTemporarilyBlocked!(this.location, tile))
                {
                    closedList.Add(hash);
                    continue;
                }

                var nextNode = new PathNode(x, y, node)
                {
                    g = (byte)nextCost
                };

                var box = new Rectangle(nextNode.x * 64 + 1, nextNode.y * 64 + 1, 62, 62);
                if (this.location.isCollidingPosition(box, Game1.viewport, true, 0, glider: false, Game1.player, pathfinding: true))
                {
                    closedList.Add(hash);
                    continue;
                }

                int priority = nextCost + Math.Abs(endPoint.X - x) + Math.Abs(endPoint.Y - y);
                closedList.Add(hash);
                openList.Enqueue(nextNode, priority);
            }

            if (++searched >= limit)
                return null;
        }

        return null;
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
        this.MoveWithoutVanillaWarps(who, time);

        this.RestoreGaitPhase(who);
    }

    /// <summary>Let AutoWalk own map transitions. Farmer.MovePosition triggers vanilla warps as
    /// soon as the bounding box touches an exit tile, which bypasses ModEntry's warp debounce.</summary>
    private void MoveWithoutVanillaWarps(Farmer who, GameTime time)
    {
        bool previous = this.ignoreWarpsField.GetValue();
        this.ignoreWarpsField.SetValue(true);
        try
        {
            who.MovePosition(time, Game1.viewport, this.location);
        }
        finally
        {
            this.ignoreWarpsField.SetValue(previous);
        }
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
