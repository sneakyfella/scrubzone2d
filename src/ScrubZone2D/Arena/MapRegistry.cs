namespace ScrubZone2D.Arena;

public static class MapRegistry
{
    private static string _directory = "";

    public static IReadOnlyList<(string Path, MapData Data)> Maps { get; private set; }
        = Array.Empty<(string, MapData)>();

    public static void Load(string directory)
    {
        _directory = directory;
        Reload();
    }

    public static void Reload()
    {
        if (!Directory.Exists(_directory))
        {
            Maps = Array.Empty<(string, MapData)>();
            return;
        }
        var list = new List<(string, MapData)>();
        foreach (var path in Directory.GetFiles(_directory, "*.json").OrderBy(f => f))
        {
            try   { list.Add((path, MapLoader.Load(path))); }
            catch { /* skip unreadable files */ }
        }
        Maps = list;
    }

    public static MapData GetOrDefault(byte index)
        => index < Maps.Count ? Maps[index].Data : new MapData();

    public static string GetPath(byte index)
    {
        if (index < Maps.Count) return Maps[index].Path;
        return Path.Combine(_directory, $"map_{index:D2}.json");
    }

    public static string NameOf(byte index)
        => index < Maps.Count ? Maps[index].Data.Name.ToUpperInvariant() : $"MAP {index}";
}
