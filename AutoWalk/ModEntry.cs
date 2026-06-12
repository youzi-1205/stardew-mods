using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Menus;
using StardewValley.Pathfinding;
using StardewValley.WorldMaps;

namespace AutoWalk;

internal sealed class ModEntry : Mod
{
    private ModConfig config = new();
    private SButton openMapKey = SButton.M;

    // Routing state.
    private bool routing;
    private string destLocation = "";
    private string destDisplayName = "";
    private Point destTile;
    private bool finalLeg;
    private Warp? legWarp;        // the warp this leg is heading for (null on the final leg)
    private Point legApproach;    // the reachable tile next to legWarp we path to
    private int idleTicks;
    private int legRetries;
    private int warpPendingTicks = -1; // >=0 while a warp transition is in progress
    private int diagTicks;
    private Vector2 lastWalkPosition;
    private int stuckTicks;
    private int npcWaitTicks;

    public override void Entry(IModHelper helper)
    {
        this.config = helper.ReadConfig<ModConfig>();
        if (!Enum.TryParse(this.config.OpenMapKey, ignoreCase: true, out this.openMapKey))
            this.openMapKey = SButton.M;

        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        helper.Events.Player.Warped += this.OnWarped;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;

        // 1) Clicking the open world map to choose a destination.
        MapPage? page = this.GetOpenMapPage();
        if (page != null)
        {
            if (e.Button == SButton.MouseLeft && this.TryResolveMapClick(page, out string loc, out Point tile, out string displayName))
            {
                this.Helper.Input.Suppress(e.Button);
                this.BeginRouting(loc, tile, displayName);
            }
            else if (e.Button == this.openMapKey)
            {
                Game1.exitActiveMenu();
                this.Helper.Input.Suppress(e.Button);
            }
            return;
        }

        // 2) Stop routing on a mouse click or movement key.
        if (this.routing)
        {
            bool mouse = e.Button == SButton.MouseLeft || e.Button == SButton.MouseRight;
            if ((this.config.StopOnMouseClick && mouse)
                || (this.config.StopOnMovementKey && IsMovementButton(e.Button)))
            {
                this.StopRouting("已停止自动寻路。", success: false);
                if (mouse)
                    this.Helper.Input.Suppress(e.Button);
                return;
            }
        }

        // 3) Open the world map.
        if (e.Button == this.openMapKey && Context.IsPlayerFree && Game1.activeClickableMenu == null)
        {
            Game1.activeClickableMenu = new GameMenu(GameMenu.mapTab, -1, playOpeningSound: false);
            this.Helper.Input.Suppress(e.Button);
        }
    }

    private void OnWarped(object? sender, WarpedEventArgs e)
    {
        if (!e.IsLocalPlayer || !this.routing)
            return;

        if (Game1.player.controller != null)
            Game1.player.controller = null;
        this.warpPendingTicks = -1;
        this.idleTicks = 0;
        this.legRetries = 0;
        this.finalLeg = false;
        this.legWarp = null;
        this.ResetStuckWatchdog();
        this.StartLeg();
    }

    /// <summary>Warp the player and mark that a warp transition is underway, so the per-tick logic
    /// doesn't fire a second warp during the fade before <see cref="OnWarped"/> arrives.</summary>
    private void DoWarp(Warp warp)
    {
        this.legWarp = null;
        this.warpPendingTicks = 0;
        this.Monitor.Log($"Warping: {Game1.currentLocation?.Name} -> {warp.TargetName} ({warp.TargetX},{warp.TargetY}).", LogLevel.Info);
        Game1.player.warpFarmer(warp);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!this.routing)
            return;

        if (!Context.IsWorldReady || Game1.eventUp)
        {
            this.StopRouting(null, success: false, silent: true);
            return;
        }

        // A warp is mid-transition (fade out/in). Wait for OnWarped instead of re-triggering it.
        if (this.warpPendingTicks >= 0)
        {
            this.warpPendingTicks++;
            if (this.warpPendingTicks > 90) // ~1.5s with no Warped event → it didn't take
            {
                this.warpPendingTicks = -1;
                this.Monitor.Log($"Warp didn't fire (still in '{Game1.currentLocation?.Name}'); re-planning.", LogLevel.Info);
                this.StartLeg();
            }
            return;
        }

        // While the controller drives the player, wait — but watch for getting stuck: if the
        // position hasn't moved for ~0.75s (blocked by an NPC/animal or a geometry trap),
        // re-path immediately instead of waiting out the controller's 5-second timeout.
        if (Game1.player.controller != null)
        {
            this.idleTicks = 0;

            // A menu pauses the single-player game: the player legitimately isn't moving, so the
            // stuck watchdog must not count those ticks as "blocked".
            if (Game1.activeClickableMenu != null && !Game1.IsMultiplayer)
                return;

            Vector2 position = Game1.player.Position;
            if (Vector2.Distance(position, this.lastWalkPosition) < 1f)
            {
                if (++this.stuckTicks >= 45)
                {
                    this.stuckTicks = 0;

                    // Pathfinding treats NPCs as passable (they move), so a villager standing on
                    // a one-tile road blocks us with no alternate route. Wait politely instead of
                    // burning retries into a bogus "unreachable" — they walk on within seconds.
                    if (IsBlockedByCharacter())
                    {
                        if (++this.npcWaitTicks <= 900) // up to ~15s of patience
                        {
                            this.Monitor.Log("Blocked by an NPC; waiting for them to move.", LogLevel.Trace);
                            return;
                        }
                    }
                    this.npcWaitTicks = 0;

                    Game1.player.controller = null;
                    if (++this.legRetries > 8)
                    {
                        this.Monitor.Log($"Giving up: stalled at {Game1.player.TilePoint}; {DescribeSurroundings()}", LogLevel.Info);
                        this.StopRouting("路被挡住了，无法到达目的地。", success: false);
                        return;
                    }
                    this.Monitor.Log($"Walk stalled at {Game1.player.TilePoint} facing={Game1.player.FacingDirection} approach={this.legApproach}; {DescribeSurroundings()}", LogLevel.Info);
                    this.StartLeg();
                    return;
                }
            }
            else
            {
                this.stuckTicks = 0;
                this.lastWalkPosition = position;
                this.legRetries = 0; // making progress again
                this.npcWaitTicks = 0;
            }

            // Snapshot the gait phase after the game animated this tick, so the controller can
            // restore it next tick after the input system's per-tick Halt wipes it.
            (Game1.player.controller as SmoothFarmerController)?.CaptureGaitPhase(Game1.player);

            if (++this.diagTicks % 60 == 0)
            {
                Farmer p = Game1.player;
                int dir = p.movementDirections.Count > 0 ? p.movementDirections[0] : -1;
                this.Monitor.Log($"walking: dir={dir} facing={p.FacingDirection} frame={p.FarmerSprite.CurrentFrame}", LogLevel.Trace);
            }
            return;
        }

        // Final leg finished → arrived.
        if (this.finalLeg)
        {
            this.StopRouting("已到达目的地。", success: true);
            return;
        }

        // Intermediate leg finished: if we got near the warp's approach tile, take the warp directly.
        if (this.legWarp != null)
        {
            Point tile = Game1.player.TilePoint;
            if (Math.Abs(tile.X - this.legApproach.X) + Math.Abs(tile.Y - this.legApproach.Y) <= 2)
            {
                this.DoWarp(this.legWarp); // OnWarped will plan the next leg
                return;
            }
        }

        // Controller stopped short of the approach tile → give it a moment, then retry / give up.
        this.idleTicks++;
        if (this.idleTicks < 10)
            return;
        this.idleTicks = 0;

        // Still close to the exit? Warping from a few tiles out beats stalling at the map edge.
        // (6 tiles: tightening this to 3 regressed the bus-stop route into hard failures.)
        if (this.legWarp != null)
        {
            Point tile = Game1.player.TilePoint;
            int distance = Math.Abs(tile.X - this.legApproach.X) + Math.Abs(tile.Y - this.legApproach.Y);
            if (distance <= 6)
            {
                this.Monitor.Log($"Stopped {distance} tiles short of the exit approach; warping anyway.", LogLevel.Info);
                this.DoWarp(this.legWarp);
                return;
            }
        }

        this.legRetries++;
        if (this.legRetries > 4)
        {
            this.StopRouting("路被挡住了，无法到达目的地。", success: false);
            return;
        }
        this.Monitor.Log($"Leg retry {this.legRetries}: at {Game1.player.TilePoint}, approach {this.legApproach}.", LogLevel.Info);
        this.StartLeg();
    }

    private void BeginRouting(string locationName, Point tile, string displayName)
    {
        if (Game1.activeClickableMenu != null)
            Game1.exitActiveMenu();

        this.destLocation = locationName;
        this.destDisplayName = string.IsNullOrWhiteSpace(displayName) ? locationName : displayName;
        this.destTile = tile;
        this.routing = true;
        this.finalLeg = false;
        this.legWarp = null;
        this.warpPendingTicks = -1;
        this.idleTicks = 0;
        this.legRetries = 0;
        this.ResetStuckWatchdog();

        this.Monitor.Log($"Routing to '{this.destDisplayName}' ({locationName} tile {tile}) from '{Game1.currentLocation?.Name}'.", LogLevel.Info);
        Game1.addHUDMessage(HUDMessage.ForCornerTextbox($"前往「{this.destDisplayName}」…"));
        this.StartLeg();
    }

    private void StartLeg()
    {
        if (!this.routing || Game1.player == null)
            return;

        GameLocation? loc = Game1.currentLocation;
        if (loc == null)
            return;

        this.ResetStuckWatchdog();

        // Final leg: we're in the destination location, walk to the chosen spot.
        if (string.Equals(loc.Name, this.destLocation, StringComparison.OrdinalIgnoreCase))
        {
            this.legWarp = null;
            if (this.destTile == Point.Zero)
            {
                this.StopRouting("已到达目的地。", success: true);
                return;
            }

            PathFindController? pfc = this.BuildFinalLegController(loc, this.destTile, out Point target);
            if (pfc == null)
                return; // pathfinder busy; retry next idle
            if (pfc.pathToEndPoint == null)
            {
                this.StopRouting("走不到这个建筑附近。", success: false);
                return;
            }
            if (pfc.pathToEndPoint.Count == 0)
            {
                this.StopRouting("已到达目的地。", success: true); // already in the area / can't reach exact spot
                return;
            }

            Game1.player.controller = pfc;
            this.finalLeg = true;
            return;
        }

        // Intermediate leg: head for the next warp toward the destination.
        Warp? warp = FindNextWarp(loc.Name, this.destLocation);
        if (warp == null)
        {
            this.StopRouting($"找不到通往「{this.destDisplayName}」的路线。", success: false);
            return;
        }

        PathFindController? legController = this.BuildWarpLegController(loc, warp, out Point approach);
        if (legController == null)
            return; // pathfinder busy; retry next idle
        if (legController.pathToEndPoint == null)
        {
            this.StopRouting($"走不到通往「{warp.TargetName}」的出口。", success: false);
            return;
        }

        this.legWarp = warp;
        this.legApproach = approach;

        if (legController.pathToEndPoint.Count == 0)
        {
            // Already standing at the exit — warp now.
            this.DoWarp(this.legWarp);
            return;
        }

        Game1.player.controller = legController;
        this.finalLeg = false;
        this.Monitor.Log($"Leg: {loc.Name} -> {warp.TargetName} via approach {approach}.", LogLevel.Trace);
    }

    private PathFindController? BuildFinalLegController(GameLocation loc, Point desiredTile, out Point target)
    {
        target = this.FindReachableTileNear(loc, desiredTile);
        PathFindController? unreachableController = null;
        int failedAttempts = 0;

        foreach (Point candidate in this.GetFinalTargetCandidates(loc, desiredTile))
        {
            PathFindController? controller = this.BuildController(candidate);
            if (controller == null)
                return null; // pathfinder's shared buffer is busy; try again next tick

            if (controller.pathToEndPoint != null)
            {
                target = candidate;
                return controller;
            }

            unreachableController ??= controller;

            // Each failed attempt is a full A* sweep of the player's whole reachable area (the
            // goal sits in a walled-off pocket, e.g. the quarry before the bridge is repaired).
            // A few nearby alternates cover the legit "door blocked by decoration" cases; trying
            // every ring tile up to radius 20 would freeze the game for seconds before failing.
            if (++failedAttempts >= 12)
                break;
        }

        return unreachableController;
    }

    private PathFindController? BuildWarpLegController(GameLocation loc, Warp warp, out Point approach)
    {
        approach = this.FindWarpApproach(loc, warp);
        PathFindController? unreachableController = null;

        int failedAttempts = 0;
        foreach (Point candidate in this.GetWarpApproachCandidates(loc, warp))
        {
            PathFindController? controller = this.BuildController(candidate);
            if (controller == null)
                return null; // pathfinder's shared buffer is busy; try again next tick

            if (controller.pathToEndPoint != null)
            {
                approach = candidate;
                return controller;
            }

            unreachableController ??= controller;

            // Same guard as the final leg: a failed A* explores the entire reachable area, so an
            // unreachable exit must not burn through ~300 ring candidates in a single frozen tick.
            if (++failedAttempts >= 8)
                break;
        }

        return unreachableController;
    }

    private PathFindController? BuildController(Point tile)
    {
        try
        {
            return new SmoothFarmerController(Game1.player, Game1.currentLocation, tile, this.config.RunWhilePathing, this.Helper.Reflection);
        }
        catch
        {
            return null; // pathfinder's shared buffer is busy; try again next tick
        }
    }

    /// <summary>Approach tile for a warp. Edge warps (map exits) get a tile scanned INWARD along
    /// the warp's own row/column — that's the road through the fence gap — so the player walks
    /// straight out the exit instead of stopping beside it and "teleporting" through scenery.</summary>
    private Point FindWarpApproach(GameLocation loc, Warp warp)
    {
        foreach (Point candidate in this.GetWarpApproachCandidates(loc, warp))
            return candidate;

        return new Point(warp.X, warp.Y);
    }

    private IEnumerable<Point> GetWarpApproachCandidates(GameLocation loc, Warp warp)
    {
        int width = loc.map?.Layers[0]?.LayerWidth ?? 0;
        int height = loc.map?.Layers[0]?.LayerHeight ?? 0;
        if (width <= 0 || height <= 0)
        {
            yield return new Point(warp.X, warp.Y);
            yield break;
        }

        int x = Math.Clamp(warp.X, 0, width - 1);
        int y = Math.Clamp(warp.Y, 0, height - 1);
        Point edge = new(x, y);

        (int dx, int dy) = warp.X < 0 ? (1, 0)
            : warp.X >= width ? (-1, 0)
            : warp.Y < 0 ? (0, 1)
            : warp.Y >= height ? (0, -1)
            : (0, 0);

        var yielded = new HashSet<Point>();
        if (dx != 0 || dy != 0)
        {
            for (int step = 0; step < 8; step++)
            {
                var candidate = new Point(x + dx * step, y + dy * step);
                if (candidate.X < 0 || candidate.Y < 0 || candidate.X >= width || candidate.Y >= height)
                    break;
                if (this.IsWalkable(loc, candidate) && yielded.Add(candidate))
                    yield return candidate;
            }
        }

        Point nearest = this.FindReachableTileNear(loc, edge);
        if (yielded.Add(nearest))
            yield return nearest;

        foreach (Point candidate in this.FindNearbyWalkableTiles(loc, edge, maxRadius: 8))
        {
            if (yielded.Add(candidate))
                yield return candidate;
        }
    }

    /// <summary>Find the tile nearest <paramref name="target"/> that's inside the map and walkable,
    /// so the pathfinder has a reachable goal even when a warp sits on the map edge.</summary>
    private Point FindReachableTileNear(GameLocation loc, Point target)
    {
        int width = loc.map?.Layers[0]?.LayerWidth ?? 0;
        int height = loc.map?.Layers[0]?.LayerHeight ?? 0;
        if (width <= 0 || height <= 0)
            return target;

        Point clamped = new(Math.Clamp(target.X, 0, width - 1), Math.Clamp(target.Y, 0, height - 1));
        if (this.IsWalkable(loc, clamped))
            return clamped;

        // The target is blocked (often the click landed ON a building). Search outward, but
        // strongly prefer tiles BELOW the target — building entrances face south, so this lands
        // us in front of the door instead of behind the structure.
        Point? bestTile = null;
        int bestScore = int.MaxValue;

        for (int radius = 1; radius <= 20; radius++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                        continue; // only the ring at this radius

                    Point candidate = new(clamped.X + dx, clamped.Y + dy);
                    if (candidate.X < 0 || candidate.Y < 0 || candidate.X >= width || candidate.Y >= height)
                        continue;
                    if (!this.IsWalkable(loc, candidate))
                        continue;

                    int distance = Math.Abs(dx) + Math.Abs(dy);
                    int northPenalty = dy < 0 ? 8 : (dy == 0 ? 3 : 0); // behind > beside > in front
                    int score = distance + northPenalty;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestTile = candidate;
                    }
                }
            }

            // A ring this close can't be beaten even by a penalty-free candidate further out.
            if (bestTile.HasValue && radius >= bestScore)
                return bestTile.Value;
        }

        return bestTile ?? clamped;
    }

    private IEnumerable<Point> GetFinalTargetCandidates(GameLocation loc, Point target)
    {
        var yielded = new HashSet<Point>();

        Point preferred = this.FindReachableTileNear(loc, target);
        if (yielded.Add(preferred))
            yield return preferred;

        foreach (Point candidate in this.FindNearbyWalkableTiles(loc, target, maxRadius: 20))
        {
            if (yielded.Add(candidate))
                yield return candidate;
        }
    }

    private IEnumerable<Point> FindNearbyWalkableTiles(GameLocation loc, Point target, int maxRadius)
    {
        int width = loc.map?.Layers[0]?.LayerWidth ?? 0;
        int height = loc.map?.Layers[0]?.LayerHeight ?? 0;
        if (width <= 0 || height <= 0)
            yield break;

        Point clamped = new(Math.Clamp(target.X, 0, width - 1), Math.Clamp(target.Y, 0, height - 1));
        for (int radius = 1; radius <= maxRadius; radius++)
        {
            var candidates = new List<Point>();
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                        continue;

                    Point candidate = new(clamped.X + dx, clamped.Y + dy);
                    if (candidate.X < 0 || candidate.Y < 0 || candidate.X >= width || candidate.Y >= height)
                        continue;
                    if (this.IsWalkable(loc, candidate))
                        candidates.Add(candidate);
                }
            }

            foreach (Point candidate in candidates.OrderBy(p => Math.Abs(p.X - clamped.X) + Math.Abs(p.Y - clamped.Y)))
                yield return candidate;
        }
    }

    private bool IsWalkable(GameLocation loc, Point tile)
    {
        var box = new Microsoft.Xna.Framework.Rectangle(tile.X * 64 + 1, tile.Y * 64 + 1, 62, 62);
        return !loc.isCollidingPosition(box, Game1.viewport, true, 0, glider: false, Game1.player, pathfinding: true);
    }

    private void StopRouting(string? message, bool success, bool silent = false)
    {
        this.routing = false;
        this.finalLeg = false;
        this.legWarp = null;
        this.destDisplayName = "";
        this.warpPendingTicks = -1;
        this.idleTicks = 0;
        this.legRetries = 0;
        this.ResetStuckWatchdog();

        if (Game1.player != null)
        {
            if (Game1.player.controller != null)
                Game1.player.controller = null;
            Game1.player.Halt();
        }

        if (!silent && message != null)
            Game1.addHUDMessage(HUDMessage.ForCornerTextbox(message));
    }

    private void ResetStuckWatchdog()
    {
        this.lastWalkPosition = Game1.player?.Position ?? Vector2.Zero;
        this.stuckTicks = 0;
        this.npcWaitTicks = 0;
    }

    /// <summary>BFS over the warp graph for the first warp on the shortest (fewest-areas) route.</summary>
    private static Warp? FindNextWarp(string fromName, string destName)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fromName };
        var queue = new Queue<string>();
        queue.Enqueue(fromName);

        var parent = new Dictionary<string, (string prev, Warp warp)>(StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            GameLocation? loc = Game1.getLocationFromName(current);
            if (loc == null)
                continue;

            foreach (Warp warp in GetOutgoingWarps(loc))
            {
                if (warp == null || warp.npcOnly.Value)
                    continue;

                string target = warp.TargetName;
                if (string.IsNullOrEmpty(target) || visited.Contains(target))
                    continue;

                visited.Add(target);
                parent[target] = (current, warp);

                if (target.Equals(destName, StringComparison.OrdinalIgnoreCase))
                    return BacktrackFirstWarp(parent, fromName, target);

                queue.Enqueue(target);
            }

            if (visited.Count > 500)
                break;
        }

        return null;
    }

    private static Warp? BacktrackFirstWarp(Dictionary<string, (string prev, Warp warp)> parent, string fromName, string dest)
    {
        string current = dest;
        Warp? firstWarp = null;
        while (parent.TryGetValue(current, out (string prev, Warp warp) step))
        {
            firstWarp = step.warp;
            if (step.prev.Equals(fromName, StringComparison.OrdinalIgnoreCase))
                return firstWarp;
            current = step.prev;
        }
        return firstWarp;
    }

    private static IEnumerable<Warp> GetOutgoingWarps(GameLocation loc)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Warp warp in loc.warps)
        {
            if (ShouldUseWarp(warp, seen))
                yield return warp;
        }

        foreach (var pair in loc.doors.Pairs)
        {
            Warp? warp = loc.getWarpFromDoor(pair.Key, Game1.player);
            if (warp != null && ShouldUseWarp(warp, seen))
                yield return warp;
        }

        foreach (Building building in loc.buildings)
        {
            if (!building.HasIndoors())
                continue;

            Warp? warp = loc.getWarpFromDoor(building.getPointForHumanDoor(), Game1.player);
            if (warp != null && ShouldUseWarp(warp, seen))
                yield return warp;
        }
    }

    private static bool ShouldUseWarp(Warp warp, HashSet<string> seen)
    {
        if (warp.npcOnly.Value || string.IsNullOrWhiteSpace(warp.TargetName))
            return false;

        string key = $"{warp.X},{warp.Y}->{warp.TargetName}:{warp.TargetX},{warp.TargetY}";
        return seen.Add(key);
    }

    private bool TryResolveMapClick(MapPage page, out string locationName, out Point tile, out string displayName)
    {
        locationName = "";
        tile = Point.Zero;
        displayName = "";

        Vector2 cursor = this.Helper.Input.GetCursorPosition().GetScaledScreenPixels();
        int mx = (int)cursor.X;
        int my = (int)cursor.Y;
        Rectangle bounds = page.mapBounds;

        MapAreaTooltip? clickedTooltip = null;
        Rectangle clickedTooltipPixels = Rectangle.Empty;
        long bestSize = long.MaxValue;

        foreach (MapArea area in page.mapAreas)
        {
            foreach (MapAreaTooltip tooltip in area.GetTooltips())
            {
                if (!IsClickableMapLabel(tooltip.Text))
                    continue;

                Rectangle pixelArea = tooltip.GetPixelArea();
                Rectangle screen = new(bounds.X + pixelArea.X, bounds.Y + pixelArea.Y, pixelArea.Width, pixelArea.Height);
                if (!screen.Contains(mx, my))
                    continue;

                long size = (long)pixelArea.Width * pixelArea.Height;
                if (size < bestSize)
                {
                    bestSize = size;
                    clickedTooltip = tooltip;
                    clickedTooltipPixels = pixelArea;
                }
            }
        }

        if (clickedTooltip == null)
            return false;

        return this.TryResolveTooltipTarget(clickedTooltip, clickedTooltipPixels, out locationName, out tile, out displayName);
    }

    private bool TryResolveTooltipTarget(MapAreaTooltip tooltip, Rectangle tooltipPixels, out string locationName, out Point tile, out string displayName)
    {
        locationName = "";
        tile = Point.Zero;
        displayName = StripTooltipDetails(tooltip.Text);

        MapAreaPosition? best = null;
        long bestScore = long.MinValue;

        foreach (MapAreaPosition position in tooltip.Area.GetWorldPositions())
        {
            if (!TryGetPositionLocationName(position, out string name))
                continue;

            Rectangle positionPixels = position.GetPixelArea();
            int overlap = GetOverlapArea(tooltipPixels, positionPixels);
            bool related = overlap > 0 || tooltipPixels.Contains(positionPixels.Center) || positionPixels.Contains(tooltipPixels.Center);
            if (!related)
                continue;

            long positionSize = Math.Max(1, (long)positionPixels.Width * positionPixels.Height);
            long score = (long)overlap * 1_000_000L - positionSize;
            if (score > bestScore)
            {
                bestScore = score;
                best = position;
                locationName = name;
            }
        }

        if (best == null)
        {
            foreach (MapAreaPosition position in tooltip.Area.GetWorldPositions())
            {
                if (!TryGetPositionLocationName(position, out string name))
                    continue;

                Rectangle positionPixels = position.GetPixelArea();
                long score = -Math.Max(1, (long)positionPixels.Width * positionPixels.Height);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = position;
                    locationName = name;
                }
            }
        }

        if (best == null)
        {
            Game1.addHUDMessage(HUDMessage.ForCornerTextbox("这个地图标记没有可寻路的位置。"));
            return false;
        }

        if (TryResolveTooltipDoorTarget(tooltip, tooltipPixels, best, locationName, out string doorLocation, out Point doorTile))
        {
            locationName = doorLocation;
            tile = doorTile;
        }
        else
        {
            tile = this.ResolveTooltipTile(best, tooltipPixels, locationName);
            if (TryGetExteriorDoorTarget(locationName, out string exteriorLocation, out Point exteriorTile))
            {
                locationName = exteriorLocation;
                tile = exteriorTile;
            }
        }

        // Some world-map position data carries tile areas that don't match the real map (e.g. the
        // spa hotspot resolved to Railroad y=197 on a ~60-tile-tall map) — clamp to actual bounds.
        tile = ClampToLocationBounds(locationName, tile);

        this.Monitor.Log($"Map hotspot '{tooltip.NamespacedId}' ('{tooltip.Text}') -> {locationName} tile {tile}.", LogLevel.Info);
        return true;
    }

    private Point ResolveTooltipTile(MapAreaPosition position, Rectangle tooltipPixels, string locationName)
    {
        Rectangle tileArea = this.GetTileArea(position, locationName);
        if (tileArea.IsEmpty)
            return Point.Zero;

        Rectangle positionPixels = position.GetPixelArea();
        if (positionPixels.Width <= 0 || positionPixels.Height <= 0)
            return tileArea.Center;

        Point center = tooltipPixels.Center;
        float xRatio = Math.Clamp((center.X - positionPixels.X) / (float)positionPixels.Width, 0f, 1f);
        float yRatio = Math.Clamp((center.Y - positionPixels.Y) / (float)positionPixels.Height, 0f, 1f);

        int x = tileArea.X + (int)Math.Round(xRatio * Math.Max(0, tileArea.Width - 1));
        int y = tileArea.Y + (int)Math.Round(yRatio * Math.Max(0, tileArea.Height - 1));
        return new Point(x, y);
    }

    private Rectangle GetTileArea(MapAreaPosition position, string locationName)
    {
        Rectangle tileArea = position.Data.TileArea;
        if (!tileArea.IsEmpty)
            return tileArea;

        GameLocation? loc = Game1.getLocationFromName(locationName);
        int width = loc?.map?.Layers[0]?.LayerWidth ?? 0;
        int height = loc?.map?.Layers[0]?.LayerHeight ?? 0;
        if (width > 0 && height > 0)
            return new Rectangle(0, 0, width, height);

        return position.Data.ExtendedTileArea ?? Rectangle.Empty;
    }

    private static bool TryResolveTooltipDoorTarget(MapAreaTooltip tooltip, Rectangle tooltipPixels, MapAreaPosition exteriorPosition, string exteriorLocationName, out string locationName, out Point tile)
    {
        locationName = "";
        tile = Point.Zero;

        if (IsGenericMapTooltip(tooltip))
            return false;

        GameLocation? exterior = Game1.getLocationFromName(exteriorLocationName);
        if (exterior == null || !exterior.IsOutdoors)
            return false;

        HashSet<string> candidates = GetTooltipTargetCandidates(tooltip);
        Warp? bestWarp = null;
        int bestScore = int.MaxValue;

        foreach (Warp warp in GetOutgoingWarps(exterior))
        {
            GameLocation? target = Game1.getLocationFromName(warp.TargetName);
            if (target == null || target.IsOutdoors)
                continue;

            bool nameMatch = IsTooltipMatchForLocation(tooltip, candidates, target);
            Point doorTile = new(warp.X, warp.Y);
            int mapDistance = GetMapPixelDistance(exteriorPosition, exterior, doorTile, tooltipPixels);
            bool geometryMatch = mapDistance <= GetDoorMatchTolerance(tooltipPixels);
            if (!nameMatch && !geometryMatch)
                continue;

            int score = mapDistance + (nameMatch ? -10_000 : 0);
            if (score < bestScore)
            {
                bestScore = score;
                bestWarp = warp;
            }
        }

        if (bestWarp != null)
        {
            locationName = exteriorLocationName;
            tile = new Point(bestWarp.X, bestWarp.Y);
            return true;
        }

        return false;
    }

    private static bool IsGenericMapTooltip(MapAreaTooltip tooltip)
    {
        return tooltip.Data.Id.Equals("Default", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTooltipMatchForLocation(MapAreaTooltip tooltip, HashSet<string> candidates, GameLocation target)
    {
        if (candidates.Contains(target.NameOrUniqueName) || candidates.Contains(target.Name))
            return true;

        string tooltipText = StripTooltipDetails(tooltip.Text);
        return IsTextMatch(tooltipText, target.DisplayName)
            || IsTextMatch(tooltipText, target.NameOrUniqueName)
            || IsTextMatch(tooltipText, target.Name);
    }

    private static HashSet<string> GetTooltipTargetCandidates(MapAreaTooltip tooltip)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTooltipTargetCandidate(candidates, tooltip.Data.Id);
        AddTooltipTargetCandidate(candidates, tooltip.NamespacedId);
        return candidates;
    }

    private static void AddTooltipTargetCandidate(HashSet<string> candidates, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return;

        string id = raw.Trim();
        int slash = id.LastIndexOf('/');
        if (slash >= 0)
            id = id[(slash + 1)..];

        if (string.IsNullOrWhiteSpace(id))
            return;

        candidates.Add(id);
        AddKnownTooltipAliases(candidates, id);

        int suffix = id.IndexOf('_');
        if (suffix > 0)
        {
            string trimmed = id[..suffix];
            candidates.Add(trimmed);
            AddKnownTooltipAliases(candidates, trimmed);
        }
    }

    private static void AddKnownTooltipAliases(HashSet<string> candidates, string id)
    {
        if (id.Equals("Spa", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("BathHouse_Entry");
            candidates.Add("BathHouse");
        }
        else if (id.Equals("Museum", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("ArchaeologyHouse");
        }
    }

    private static int GetMapPixelDistance(MapAreaPosition position, GameLocation location, Point tile, Rectangle tooltipPixels)
    {
        Vector2 mapPixel = position.GetMapPixelPosition(location, tile);
        return GetDistanceToRectangle(tooltipPixels, mapPixel);
    }

    private static int GetDistanceToRectangle(Rectangle rectangle, Vector2 point)
    {
        int x = (int)Math.Round(point.X);
        int y = (int)Math.Round(point.Y);
        int dx = x < rectangle.Left ? rectangle.Left - x : x > rectangle.Right ? x - rectangle.Right : 0;
        int dy = y < rectangle.Top ? rectangle.Top - y : y > rectangle.Bottom ? y - rectangle.Bottom : 0;
        return dx + dy;
    }

    private static int GetDoorMatchTolerance(Rectangle tooltipPixels)
    {
        int size = Math.Max(tooltipPixels.Width, tooltipPixels.Height);
        return Math.Clamp(size / 3, 12, 48);
    }

    private static string StripTooltipDetails(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        string firstLine = text.Split('\n', '\r')[0].Trim();
        return firstLine.Length > 0 ? firstLine : text.Trim();
    }

    private static bool IsTextMatch(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return left.Contains(right, StringComparison.OrdinalIgnoreCase)
            || right.Contains(left, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetExteriorDoorTarget(string locationName, out string exteriorLocation, out Point exteriorTile)
    {
        exteriorLocation = "";
        exteriorTile = Point.Zero;

        GameLocation? location = Game1.getLocationFromName(locationName);
        if (location == null || location.IsOutdoors)
            return false;

        // Among the exits leading outdoors, prefer the one with the LARGEST target Y — that's the
        // front/south door in practice, so we stop at the entrance instead of a back exit.
        int bestY = int.MinValue;
        foreach (Warp warp in location.warps)
        {
            if (warp.npcOnly.Value || string.IsNullOrWhiteSpace(warp.TargetName))
                continue;

            GameLocation? target = Game1.getLocationFromName(warp.TargetName);
            if (target == null || !target.IsOutdoors)
                continue;

            if (warp.TargetY > bestY)
            {
                bestY = warp.TargetY;
                exteriorLocation = warp.TargetName;
                exteriorTile = new Point(warp.TargetX, warp.TargetY);
            }
        }

        return bestY != int.MinValue;
    }

    private static bool IsClickableMapLabel(string text)
    {
        return !string.IsNullOrWhiteSpace(text) && text != "???";
    }

    private static Point ClampToLocationBounds(string locationName, Point tile)
    {
        GameLocation? location = Game1.getLocationFromName(locationName);
        int width = location?.map?.Layers[0]?.LayerWidth ?? 0;
        int height = location?.map?.Layers[0]?.LayerHeight ?? 0;
        if (width <= 0 || height <= 0)
            return tile;

        return new Point(Math.Clamp(tile.X, 0, width - 1), Math.Clamp(tile.Y, 0, height - 1));
    }

    private static bool TryGetPositionLocationName(MapAreaPosition position, out string name)
    {
        name = position.Data.LocationName;
        if (string.IsNullOrEmpty(name) && position.Data.LocationNames.Count > 0)
            name = position.Data.LocationNames[0];
        return !string.IsNullOrWhiteSpace(name);
    }

    private static int GetOverlapArea(Rectangle a, Rectangle b)
    {
        int left = Math.Max(a.Left, b.Left);
        int top = Math.Max(a.Top, b.Top);
        int right = Math.Min(a.Right, b.Right);
        int bottom = Math.Min(a.Bottom, b.Bottom);
        return right <= left || bottom <= top ? 0 : (right - left) * (bottom - top);
    }

    private MapPage? GetOpenMapPage()
    {
        return Game1.activeClickableMenu switch
        {
            MapPage mapPage => mapPage,
            GameMenu gameMenu when gameMenu.currentTab == GameMenu.mapTab => gameMenu.GetCurrentPage() as MapPage,
            _ => null
        };
    }

    private static bool IsMovementButton(SButton button)
    {
        return button is SButton.W or SButton.A or SButton.S or SButton.D
            or SButton.Up or SButton.Down or SButton.Left or SButton.Right;
    }

    /// <summary>Diagnostic snapshot of the tile the player is pushing into: what's on it, and
    /// whether the movement vs pathfinding collision checks disagree (the smoking gun for any
    /// "A* says walkable, body says blocked" stall).</summary>
    private static string DescribeSurroundings()
    {
        Farmer player = Game1.player;
        GameLocation? location = Game1.currentLocation;
        if (location == null)
            return "no location";

        Rectangle next = player.nextPosition(player.FacingDirection);
        var tile = new Vector2(next.Center.X / 64, next.Center.Y / 64);

        string objName = location.objects.TryGetValue(tile, out StardewValley.Object? obj) ? obj.Name : "-";
        string featureName = location.terrainFeatures.TryGetValue(tile, out var feature) ? feature.GetType().Name : "-";

        string npcName = "-";
        foreach (NPC npc in location.characters)
        {
            if (npc.GetBoundingBox().Intersects(next))
            {
                npcName = $"{npc.Name}(passThrough={npc.farmerPassesThrough})";
                break;
            }
        }

        bool collideMove = location.isCollidingPosition(next, Game1.viewport, true, 0, glider: false, player);
        bool collidePath = location.isCollidingPosition(next, Game1.viewport, true, 0, glider: false, player, pathfinding: true);

        return $"ahead tile={tile} obj={objName} feature={featureName} npc={npcName} collide(move)={collideMove} collide(path)={collidePath}";
    }

    /// <summary>Is a character (villager/animal/horse) standing right next to the player —
    /// i.e. the likely reason we can't move?</summary>
    private static bool IsBlockedByCharacter()
    {
        GameLocation? location = Game1.currentLocation;
        if (location == null)
            return false;

        Microsoft.Xna.Framework.Rectangle reach = Game1.player.GetBoundingBox();
        reach.Inflate(56, 56);

        foreach (NPC npc in location.characters)
        {
            if (npc.GetBoundingBox().Intersects(reach))
                return true;
        }
        return false;
    }
}
