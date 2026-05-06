using MonoGameTemplate.Diagnostics;
using ScrubZone2D;

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Logger.WriteCrashDump(e.ExceptionObject as Exception);

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Logger.WriteCrashDump(e.Exception);
    e.SetObserved();
};

// Parse --name <value> from command line
string? playerName = null;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--name")
    {
        playerName = args[i + 1];
        break;
    }
}

try
{
    using var game = new Game1(playerName);
    game.Run();
}
catch (Exception ex)
{
    Logger.WriteCrashDump(ex);
    throw;
}
