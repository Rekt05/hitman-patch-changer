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
    public const string RemoteUrl = "https://raw.githubusercontent.com/Rekt05/hitman-patch-changer/main/patches.json";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public static string CachePath => Path.Combine(Paths.DataDir, "patches-cache.json");

    public static PatchFile Load(string? contentRoot = null)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "patches.json"),
            Path.Combine(contentRoot ?? Directory.GetCurrentDirectory(), "patches.json")
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path != null)
            return Parse(File.ReadAllText(path))
                ?? throw new InvalidDataException("patches.json is empty");
        return ParseEmbedded()
            ?? throw new InvalidDataException("embedded patches.json is empty");
    }

    public static PatchFile? TryLoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            return Parse(File.ReadAllText(CachePath));
        }
        catch
        {
            return null;
        }
    }

    public static void SaveCache(PatchFile file)
    {
        Directory.CreateDirectory(Paths.DataDir);
        var tmp = CachePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, Paths.Json));
        File.Move(tmp, CachePath, overwrite: true);
    }

    public static async Task<PatchFile?> FetchRemoteAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, RemoteUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", "HitmanPatchChanger");
            req.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return null;
            var json = await res.Content.ReadAsStringAsync(ct);
            if (json.Length == 0 || json.Length > 1_000_000) return null;
            return Parse(json);
        }
        catch
        {
            return null;
        }
    }

    public static PatchFile Merge(PatchFile local, PatchFile extra)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var patches = new List<PatchEntry>();
        foreach (var p in extra.Patches.Concat(local.Patches))
        {
            if (!seen.Add(p.Id)) continue;
            patches.Add(p);
        }
        return new PatchFile
        {
            AppId = local.AppId,
            DepotId = local.DepotId,
            SteamAppId = local.SteamAppId,
            Patches = patches
        };
    }

    public static bool SameList(PatchFile a, PatchFile b)
    {
        if (a.Patches.Count != b.Patches.Count) return false;
        for (var i = 0; i < a.Patches.Count; i++)
        {
            var x = a.Patches[i];
            var y = b.Patches[i];
            if (!x.Id.Equals(y.Id, StringComparison.OrdinalIgnoreCase)) return false;
            if (!x.Version.Equals(y.Version, StringComparison.Ordinal)) return false;
            if (!x.SteamManifestId.Equals(y.SteamManifestId, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    public static PatchEntry? Find(PatchFile file, string idOrVersionOrManifest)
    {
        var key = idOrVersionOrManifest.Trim();
        return file.Patches.FirstOrDefault(p => p.Id.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? file.Patches.FirstOrDefault(p => p.SteamManifestId.Length > 0 && p.SteamManifestId.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? file.Patches.FirstOrDefault(p => p.Version.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    static PatchFile? ParseEmbedded()
    {
        var asm = typeof(PatchCatalog).Assembly;
        var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("patches.json", StringComparison.OrdinalIgnoreCase));
        if (resName == null) throw new FileNotFoundException("patches.json not found");
        using var res = asm.GetManifestResourceStream(resName)!;
        using var reader = new StreamReader(res);
        return Parse(reader.ReadToEnd());
    }

    static PatchFile? Parse(string json)
    {
        var file = JsonSerializer.Deserialize<PatchFile>(json, Paths.Json);
        if (file == null) return null;
        var patches = new List<PatchEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in file.Patches)
        {
            var id = p.Id.Trim();
            var version = p.Version.Trim();
            var manifest = p.SteamManifestId.Trim();
            if (id.Length == 0 || version.Length == 0 || manifest.Length == 0) continue;
            if (!manifest.All(char.IsDigit)) continue;
            if (!seen.Add(id)) continue;
            patches.Add(new PatchEntry { Id = id, Version = version, SteamManifestId = manifest });
        }
        if (patches.Count == 0) return null;
        file.Patches = patches;
        return file;
    }
}

public sealed class LiveCatalog
{
    readonly object _gate = new();
    PatchFile _current;

    public LiveCatalog(PatchFile initial) => _current = initial;

    public PatchFile Current
    {
        get { lock (_gate) return _current; }
    }

    public void Replace(PatchFile next)
    {
        lock (_gate) _current = next;
    }
}
