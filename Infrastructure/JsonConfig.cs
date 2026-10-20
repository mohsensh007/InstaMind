using System.Text.Encodings.Web;
using System.Text.Json;

namespace InstaMind.Infrastructure;

public static class JsonConfig
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };
}

public static class AtomicJson
{
    public static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, JsonConfig.Options);
        }
        catch
        {
            return null;
        }
    }

    public static void Write<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonConfig.Options));
        File.Move(tmp, path, overwrite: true);
    }
}
