using System.Text.Json;
using System.Text.Json.Serialization;

namespace HitmanPatchChanger;

public sealed class AppConfig
{
    public string InstallDir { get; set; } = "";
    public string ToolDir { get; set; } = "";
    public string Username { get; set; } = "";
    public uint AppId { get; set; } = 1847520;
    public uint DepotId { get; set; } = 1659041;
    public uint SteamAppId { get; set; } = 1659040;
    public string? LastVersion { get; set; }
    public string? LastManifestId { get; set; }
    public int Port { get; set; } = 3847;
    public int MaxDownloads { get; set; } = 8;
    public int? CellId { get; set; }
}

public sealed class PatchEntry
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string SteamManifestId { get; set; } = "";
}

public sealed class PatchFile
{
    public uint AppId { get; set; }
    public uint DepotId { get; set; }
    public uint SteamAppId { get; set; }
    public List<PatchEntry> Patches { get; set; } = [];
}

public static class Paths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "HitmanPatchChanger");

    public static string ConfigPath => Path.Combine(DataDir, "config.json");
    public static string DepotDownloaderFolder => Path.Combine(DataDir, "depotdownloader");
    public static string LoginScratch => Path.Combine(DataDir, "login-scratch");
    public static string ConfigLock { get; } = Path.Combine(DataDir, "config.lock");

    public static string ExpandHome(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0) return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(home)) return path;
        if (path == "~") return home;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(home, path[2..]);
        return path;
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

public static class ConfigStore
{
    public static AppConfig Load()
    {
        Directory.CreateDirectory(Paths.DataDir);
        if (File.Exists(Paths.ConfigPath))
        {
            var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Paths.ConfigPath), Paths.Json);
            if (loaded != null) return loaded;
        }

        var cfg = new AppConfig();
        Save(cfg);
        return cfg;
    }

    public static void Save(AppConfig cfg)
    {
        Directory.CreateDirectory(Paths.DataDir);
        var tmp = Paths.ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Paths.Json));
        File.Move(tmp, Paths.ConfigPath, overwrite: true);
    }
}

public static class PatchCatalog
{
    public static PatchFile Load(string? contentRoot = null)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "patches.json"),
            Path.Combine(contentRoot ?? Directory.GetCurrentDirectory(), "patches.json")
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path != null)
            return JsonSerializer.Deserialize<PatchFile>(File.ReadAllText(path), Paths.Json)
                ?? throw new InvalidDataException("patches.json is empty");
        var asm = typeof(PatchCatalog).Assembly;
        var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("patches.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("patches.json not found");
        using var res = asm.GetManifestResourceStream(resName)!;
        return JsonSerializer.Deserialize<PatchFile>(res, Paths.Json)
            ?? throw new InvalidDataException("embedded patches.json is empty");
    }

    public static PatchEntry? Find(PatchFile file, string idOrVersionOrManifest)
    {
        var key = idOrVersionOrManifest.Trim();
        return file.Patches.FirstOrDefault(p => p.Id.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? file.Patches.FirstOrDefault(p => p.SteamManifestId.Length > 0 && p.SteamManifestId.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? file.Patches.FirstOrDefault(p => p.Version.Equals(key, StringComparison.OrdinalIgnoreCase));
    }
}
