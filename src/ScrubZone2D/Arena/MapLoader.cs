using System.Text.Json;

namespace ScrubZone2D.Arena;

public static class MapLoader
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented               = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static MapData Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<MapData>(json, Opts) ?? new MapData();
    }

    public static void Save(MapData data, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(data, Opts));
    }
}
