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
    private Point destTile;
    private bool finalLeg;
    private Warp? legWarp;        // the warp this leg is heading for (null on the final leg)
    private Point legApproach;    // the reachable tile next to legWarp we path to
    private int idleTicks;
    private int legRetries;
    private int warpPendingTicks = -1; // >=0 while a warp transition is in progress
    private int diagTicks;

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
            if (e.Button == SButton.MouseLeft && this.TryResolveMapClick(page, out string loc, out Point tile))
            {
                this.Helper.Input.Suppress(e.Button);
                this.BeginRouting(loc, tile);
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
        this.StartLeg();
    }

    /// <summary>Warp the player and mark that a warp transition is underway, so the per-tick logic
    /// doesn't fire a second warp during the fade before <see cref="OnWarped"/> arrives.</summary>
    private void DoWarp(Warp warp)
    {
        this.legWarp = null;
        this.warpPendingTicks = 0;
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
            if (this.warpPendingTicks > 180) // ~3s with no Warped event → assume it didn't take
            {
                this.warpPendingTicks = -1;
                this.StartLeg();
            }
            return;
        }

        // While the controller drives the player, wait.
        if (Game1.player.controller != null)
        {
            this.idleTicks = 0;

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
        if (this.idleTicks < 15)
            return;
        this.idleTicks = 0;

        this.legRetries++;
        if (this.legRetries > 4)
        {
            this.StopRouting("路被挡住了，无法到达目的地。", success: false);
            return;
        }
        this.StartLeg();
    }

    private void BeginRouting(string locationName, Point tile)
    {
        if (Game1.activeClickableMenu != null)
            Game1.exitActiveMenu();

        this.destLocation = locationName;
        this.destTile = tile;
        this.routing = true;
        this.finalLeg = false;
        this.legWarp = null;
        this.warpPendingTicks = -1;
        this.idleTicks = 0;
        this.legRetries = 0;

        this.Monitor.Log($"Routing to '{locationName}' tile {tile} from '{Game1.currentLocation?.Name}'.", LogLevel.Info);
        Game1.addHUDMessage(HUDMessage.ForCornerTextbox($"前往「{locationName}」…"));
        this.StartLeg();
    }

    private void StartLeg()
    {
        if (!this.routing || Game1.player == null)
            return;

        GameLocation? loc = Game1.currentLocation;
        if (loc == null)
            return;

        // Final leg: we're in the destination location, walk to the chosen spot.
        if (string.Equals(loc.Name, this.destLocation, StringComparison.OrdinalIgnoreCase))
        {
            this.legWarp = null;
            if (this.destTile == Point.Zero)
            {
                this.StopRouting("已到达目的地。", success: true);
                return;
            }

            Point target = this.FindReachableTileNear(loc, this.destTile);
            PathFindController? pfc = this.BuildController(target);
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
            this.StopRouting($"找不到通往「{this.destLocation}」的路线。", success: false);
            return;
        }

        Point approach = this.FindReachableTileNear(loc, new Point(warp.X, warp.Y));
        this.legWarp = warp;
        this.legApproach = approach;

        PathFindController? legController = this.BuildController(approach);
        if (legController == null)
            return; // pathfinder busy; retry next idle
        if (legController.pathToEndPoint == null)
        {
            this.StopRouting($"走不到通往「{warp.TargetName}」的出口。", success: false);
            return;
        }
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

    private PathFindController? BuildController(Point tile)
    {
        try
        {
            return new SmoothFarmerController(Game1.player, Game1.currentLocation, tile, this.config.RunWhilePathing);
        }
        catch
        {
            return null; // pathfinder's shared buffer is busy; try again next tick
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
        this.warpPendingTicks = -1;
        this.idleTicks = 0;
        this.legRetries = 0;

        if (Game1.player != null)
        {
            if (Game1.player.controller != null)
                Game1.player.controller = null;
            Game1.player.Halt();
        }

        if (!silent && message != null)
            Game1.addHUDMessage(HUDMessage.ForCornerTextbox(message));
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

    private bool TryResolveMapClick(MapPage page, out string locationName, out Point tile)
    {
        locationName = "";
        tile = Point.Zero;

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

        return this.TryResolveTooltipTarget(clickedTooltip, clickedTooltipPixels, out locationName, out tile);
    }

    private bool TryResolveTooltipTarget(MapAreaTooltip tooltip, Rectangle tooltipPixels, out string locationName, out Point tile)
    {
        locationName = "";
        tile = Point.Zero;

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

        tile = this.ResolveTooltipTile(best, tooltipPixels, locationName);
        if (TryGetExteriorDoorTarget(locationName, out string exteriorLocation, out Point exteriorTile))
        {
            locationName = exteriorLocation;
            tile = exteriorTile;
        }

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
}
