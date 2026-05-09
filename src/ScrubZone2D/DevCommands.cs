#if !SHIPPING
using MonoGameTemplate.DevConsole;
using ScrubZone2D.Arena;
using ScrubZone2D.Network;

namespace ScrubZone2D;

public static class DevCommands
{
    public static void Register(CommandRegistry registry)
    {
        registry.Register(new NetStatusCommand());
        registry.Register(new NetDisconnectCommand());
        registry.Register(new MapListCommand());
        registry.Register(new MapReloadCommand());
        registry.Register(new QuitCommand());
    }
}

sealed class NetStatusCommand : IConsoleCommand
{
    public string Name  => "net.status";
    public string Usage => "print current network state";

    public void Execute(string[] args, Action<string, ConsoleMessageType> print)
    {
        var net = NetworkManager.Instance;
        print($"Role:       {net.Role}",    ConsoleMessageType.Info);
        print($"Connected:  {net.IsConnected}", ConsoleMessageType.Info);
        print($"LocalName:  {net.LocalName}",   ConsoleMessageType.Info);
        print($"RemoteName: {net.RemoteName}",  ConsoleMessageType.Info);
        print($"GameMode:   {net.GameMode}",    ConsoleMessageType.Info);
        print($"Map:        [{net.SelectedMap}] {MapRegistry.NameOf(net.SelectedMap)}",
            ConsoleMessageType.Info);
        print($"GameStarted:{net.GameStarted}", ConsoleMessageType.Info);
        if (net.StatusText is not null)
            print($"Status:     {net.StatusText}", ConsoleMessageType.Info);
    }
}

sealed class NetDisconnectCommand : IConsoleCommand
{
    public string Name  => "net.disconnect";
    public string Usage => "force-dispose the network session";

    public void Execute(string[] args, Action<string, ConsoleMessageType> print)
    {
        NetworkManager.Instance.Dispose();
        print("Network session disposed.", ConsoleMessageType.Warning);
    }
}

sealed class MapListCommand : IConsoleCommand
{
    public string Name  => "map.list";
    public string Usage => "list all loaded maps";

    public void Execute(string[] args, Action<string, ConsoleMessageType> print)
    {
        var maps = MapRegistry.Maps;
        if (maps.Count == 0) { print("No maps loaded.", ConsoleMessageType.Warning); return; }
        for (int i = 0; i < maps.Count; i++)
        {
            var (path, data) = maps[i];
            print($"[{i}] {data.Name,-20} {data.WorldWidth}x{data.WorldHeight}  {Path.GetFileName(path)}",
                ConsoleMessageType.Info);
        }
    }
}

sealed class MapReloadCommand : IConsoleCommand
{
    public string Name  => "map.reload";
    public string Usage => "reload all map JSON files from disk";

    public void Execute(string[] args, Action<string, ConsoleMessageType> print)
    {
        int before = MapRegistry.Maps.Count;
        MapRegistry.Reload();
        int after  = MapRegistry.Maps.Count;
        print($"Reloaded {after} map(s) (was {before}).", ConsoleMessageType.Success);
    }
}

sealed class QuitCommand : IConsoleCommand
{
    public string Name  => "quit";
    public string Usage => "exit the application";

    public void Execute(string[] args, Action<string, ConsoleMessageType> print)
    {
        print("Goodbye.", ConsoleMessageType.Info);
        Environment.Exit(0);
    }
}
#endif
