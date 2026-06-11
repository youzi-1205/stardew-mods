using System.Runtime.InteropServices;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace NoTerminal;

internal sealed class ModConfig
{
    // Hide the SMAPI console window. Logs are still written to ErrorLogs/SMAPI-latest.txt.
    public bool HideConsole { get; set; } = true;
}

/// <summary>Hides the SMAPI console window once the game launches. The console belongs to the
/// same process, so a simple Win32 ShowWindow(SW_HIDE) on our own console handle does it —
/// equivalent to launching with --no-terminal, but without asking players to edit launch options.</summary>
internal sealed class ModEntry : Mod
{
    private const int SW_HIDE = 0;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public override void Entry(IModHelper helper)
    {
        ModConfig config = helper.ReadConfig<ModConfig>();
        if (!config.HideConsole)
            return;

        helper.Events.GameLoop.GameLaunched += (_, _) =>
        {
            IntPtr console = GetConsoleWindow();
            if (console != IntPtr.Zero)
            {
                ShowWindow(console, SW_HIDE);
                this.Monitor.Log("SMAPI console hidden (logs still go to ErrorLogs/SMAPI-latest.txt).", LogLevel.Info);
            }
        };
    }
}
