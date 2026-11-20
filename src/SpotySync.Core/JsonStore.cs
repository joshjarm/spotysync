using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpotySync;

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static T Load<T>(string path, Func<T> fallback)
    {
        try
        {
            if (!File.Exists(path)) return fallback();
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? fallback();
        }
        catch
        {
            return fallback();
        }
    }

    public static void SaveAtomic<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, true);
    }
}
