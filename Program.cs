using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HitmanPatchChanger;
using Microsoft.Extensions.FileProviders;

var catalog = PatchCatalog.Load();
var hub = new EventHub();
var logs = new LogBuffer(hub);
var cfg = ConfigStore.Load();
if (string.IsNullOrWhiteSpace(cfg.ToolDir)
    && File.Exists(Path.Combine(Paths.DepotDownloaderFolder, OperatingSystem.IsWindows() ? "DepotDownloader.exe" : "DepotDownloader")))
{
    cfg.ToolDir = Paths.DepotDownloaderFolder;
    ConfigStore.Save(cfg);
}
var port = ResolvePort(args, cfg);
var depot = new DepotClient(hub, logs, steamLoginId: SteamLoginId(port));
var app = Build(args, catalog, hub, logs, depot, port);

var url = $"http://127.0.0.1:{port}";

depot.ProbeBinary();

Console.WriteLine("Hitman WoA Version Switcher");
Console.WriteLine($"Opens: {url}");
try
{
    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
catch
{
    Console.WriteLine("Could not open a browser automatically - open the URL above.");
}

app.Lifetime.ApplicationStopping.Register(() =>
{
    depot.Cancel();
    depot.Dispose();
});

await app.RunAsync();

static WebApplication Build(string[] args, PatchFile catalog, EventHub hub, LogBuffer logs, DepotClient depot, int port)
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(o =>
    {
        o.TimestampFormat = "HH:mm:ss ";
        o.SingleLine = true;
    });
    builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

    var app = builder.Build();
    var physical = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
    IFileProvider files = Directory.Exists(physical)
        ? new CompositeFileProvider(new PhysicalFileProvider(physical), new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot"))
        : new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot");
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });

    app.MapGet("/api/state", () => Results.Json(BuildState(catalog, depot, port), Paths.Json));
    app.MapGet("/api/patches", () => Results.Json(catalog, Paths.Json));
    app.MapGet("/api/logs", () => Results.Json(logs.Recent, Paths.Json));

    app.MapGet("/api/events", async (HttpContext ctx, CancellationToken ct) =>
    {
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        await ctx.Response.WriteAsync("retry: 2000\n\n", ct);
        await WriteSse(ctx, "state", BuildState(catalog, depot, port), ct);
        await foreach (var ev in hub.Subscribe(ct))
        {
            await WriteSse(ctx, ev.Name, ev.Payload, ct);
        }
    });

    app.MapPost("/api/settings", (SettingsRequest req) =>
    {
        var cfg = ConfigStore.Load();
        if (req.InstallDir != null)
        {
            var dir = req.InstallDir.Trim().Trim('"');
            cfg.InstallDir = dir;
            if (req.CreateIfMissing == true && !string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
        }
        if (req.Username != null) cfg.Username = req.Username.Trim();
        if (req.ToolDir != null) cfg.ToolDir = req.ToolDir.Trim().Trim('"');
        ConfigStore.Save(cfg);
        depot.ProbeBinary();
        return Results.Json(BuildState(catalog, depot, port), Paths.Json);
    });

    app.MapPost("/api/login", (LoginRequest req) =>
    {
        try
        {
            var cfg = ConfigStore.Load();
            if (InstallProbe.BlockReason(cfg.InstallDir) is { } installBlock)
                return Results.Json(new { ok = false, error = installBlock }, statusCode: 400);
            if (depot.ToolBlockReason() is { } toolBlock)
                return Results.Json(new { ok = false, error = toolBlock }, statusCode: 400);
            if (!string.IsNullOrWhiteSpace(req.Username))
            {
                cfg.Username = req.Username.Trim();
                ConfigStore.Save(cfg);
            }
            var qr = req.Qr;
            var user = qr ? null : (req.Username ?? cfg.Username);
            var pass = req.Password;
            if (!qr && string.IsNullOrWhiteSpace(user))
                return Results.Json(new { ok = false, error = "Enter a Steam username, or use Log in via QR code." }, statusCode: 400);
            if (depot.IsTransferring)
                return Results.Json(new { ok = false, error = "A download is in progress. Cancel it before signing in." }, statusCode: 409);
            depot.KeepSwitchForResume();
            var previous = depot.PrepareReplace(keepResume: true, allowLoginJob: true);
            var job = Task.Run(async () =>
            {
                try
                {
                    await previous;
                    await PrepareJobFolders(depot, cfg);
                    await depot.LoginAsync(cfg, user, pass, qr);
                }
                catch (Exception ex) { depot.Fail("login-failed", ex.Message); }
            });
            depot.StoreRunningTask(job);
            return Results.Json(new { ok = true, started = true });
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
        }
    });

    app.MapPost("/api/guard", (GuardRequest req) =>
    {
        if (string.IsNullOrWhiteSpace(req.Code))
            return Results.Json(new { ok = false, error = "Code is empty." }, statusCode: 400);
        var ok = depot.SubmitInput(req.Code);
        return Results.Json(new { ok });
    });

    app.MapPost("/api/switch", (SwitchRequest req) =>
    {
        var cfg = ConfigStore.Load();
        PatchEntry? patch = null;
        if (!string.IsNullOrWhiteSpace(req.PatchId))
            patch = PatchCatalog.Find(catalog, req.PatchId);
        if (patch == null && !string.IsNullOrWhiteSpace(req.ManifestId))
        {
            var key = req.ManifestId.Trim();
            patch = catalog.Patches.FirstOrDefault(p => p.SteamManifestId == key)
                ?? new PatchEntry { Id = "custom", Version = "custom", SteamManifestId = key };
        }
        if (patch == null)
            return Results.Json(new { ok = false, error = "Unknown patch. Pick one from the list or paste a manifest ID." }, statusCode: 400);
        if (InstallProbe.BlockReason(cfg.InstallDir) is { } installBlock)
            return Results.Json(new { ok = false, error = installBlock }, statusCode: 400);
        if (depot.ToolBlockReason() is { } toolBlock)
            return Results.Json(new { ok = false, error = toolBlock }, statusCode: 400);
        if (patch.SteamManifestId.Length == 0)
            return Results.Json(new { ok = false, error = "This patch has no Steam manifest." }, statusCode: 400);
        if (string.IsNullOrWhiteSpace(cfg.Username))
            return Results.Json(new { ok = false, error = "Log in to Steam first so a username is saved." }, statusCode: 400);
        if (depot.IsTransferring)
            return Results.Json(new { ok = false, error = "A download is in progress. Cancel it before switching to another version." }, statusCode: 409);
        var target = patch;
        if (depot.IsLoggingIn)
        {
            depot.QueueResume(target);
            return Results.Json(new { ok = true, queued = true, version = patch.Version });
        }
        var previous = depot.PrepareReplace(keepResume: false, allowLoginJob: false);
        var job = Task.Run(async () =>
        {
            try
            {
                await previous;
                await PrepareJobFolders(depot, cfg);
                await depot.SwitchAsync(cfg, target);
            }
            catch (Exception ex) { depot.Fail("switch-failed", ex.Message); }
        });
        depot.StoreRunningTask(job);
        return Results.Json(new { ok = true, started = true, version = patch.Version });
    });

    app.MapPost("/api/cancel", () =>
    {
        depot.Cancel();
        return Results.Json(new { ok = true });
    });

    app.MapPost("/api/setup-tool", async (SetupToolRequest req) =>
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(req.Path))
                depot.UseToolDir(req.Path);
            else
                await depot.EnsureBinaryAsync();
            return Results.Json(BuildState(catalog, depot, port), Paths.Json);
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
        }
    });

    app.MapPost("/api/browse", async (BrowseRequest req) =>
    {
        if (!OperatingSystem.IsWindows())
            return Results.Json(new { ok = false, error = "Type the path manually on this OS." }, statusCode: 400);
        if (!await BrowseGate.Lock.WaitAsync(0))
            return Results.Json(new { ok = false, error = "A picker window is already open. Check your taskbar." }, statusCode: 409);
        try
        {
            var path = await BrowseDialog(req.Kind == "file");
            return Results.Json(new { ok = true, path });
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = ex.Message }, statusCode: 400);
        }
        finally
        {
            BrowseGate.Lock.Release();
        }
    });

    app.MapPost("/api/open-folder", (OpenRequest req) =>
    {
        var cfg = ConfigStore.Load();
        if (string.IsNullOrWhiteSpace(cfg.InstallDir))
            return Results.Json(new { ok = false, error = "No install directory set." }, statusCode: 400);
        var path = req.Which?.Equals("retail", StringComparison.OrdinalIgnoreCase) == true
            ? Path.Combine(cfg.InstallDir, "Retail")
            : cfg.InstallDir;
        if (!Directory.Exists(path))
            return Results.Json(new { ok = false, error = "Folder does not exist: " + path }, statusCode: 404);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "explorer.exe" : path,
                Arguments = OperatingSystem.IsWindows() ? $"\"{path}\"" : "",
                UseShellExecute = true
            });
            return Results.Json(new { ok = true });
        }
        catch (Exception ex)
        {
            return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500);
        }
    });

    return app;
}

static async Task PrepareJobFolders(DepotClient depot, AppConfig cfg)
{
    if (!string.IsNullOrWhiteSpace(cfg.InstallDir))
        Directory.CreateDirectory(cfg.InstallDir);
    if (!depot.BinaryPresent)
        await depot.EnsureBinaryAsync();
    if (!depot.ProbeBinary())
        throw new InvalidOperationException(depot.ToolsError ?? "DepotDownloader did not start.");
}

static UiState BuildState(PatchFile catalog, DepotClient depot, int port)
{
    var cfg = ConfigStore.Load();
    var installOk = !string.IsNullOrWhiteSpace(cfg.InstallDir) && Directory.Exists(cfg.InstallDir);
    return new UiState
    {
        Config = cfg,
        DetectedVersion = InstallProbe.DetectVersion(cfg, catalog),
        CachePresent = installOk && InstallProbe.CachePresent(cfg.InstallDir),
        InstallExists = installOk,
        InstallCompatible = installOk && InstallProbe.Compatible(cfg.InstallDir),
        ToolDirExists = !string.IsNullOrWhiteSpace(cfg.ToolDir) && Directory.Exists(cfg.ToolDir),
        SteamAppIdPresent = installOk && InstallProbe.SteamAppIdPresent(cfg.InstallDir),
        AccountConfigPresent = depot.AccountConfigPresent,
        SessionLikelyValid = depot.AccountConfigPresent,
        DepotDownloader = depot.ToolsStatus,
        DepotDownloaderPath = depot.ToolsPath,
        DepotDownloaderVersion = depot.ToolsVersion,
        ToolPath = depot.ToolsPath,
        Session = depot.Snapshot(),
        ListenUrl = $"http://127.0.0.1:{port}"
    };
}

static int ResolvePort(string[] args, AppConfig cfg)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if ((args[i] == "--port" || args[i] == "-p")
            && int.TryParse(args[i + 1], out var parsed)
            && parsed is > 0 and < 65536)
            return parsed;
    }
    if (int.TryParse(Environment.GetEnvironmentVariable("HITMAN_PATCH_PORT"), out var env)
        && env is > 0 and < 65536)
        return env;
    return cfg.Port is > 0 and < 65536 ? cfg.Port : 3847;
}

static int SteamLoginId(int port) => port * 10000 + 1;

static async Task WriteSse(HttpContext ctx, string name, object payload, CancellationToken ct)
{
    var json = JsonSerializer.Serialize(payload, Paths.Json);
    await ctx.Response.WriteAsync($"event: {name}\n", ct);
    foreach (var line in json.Split('\n'))
        await ctx.Response.WriteAsync($"data: {line}\n", ct);
    await ctx.Response.WriteAsync("\n", ct);
    await ctx.Response.Body.FlushAsync(ct);
}

static async Task<string?> BrowseDialog(bool pickFile)
{
    if (!OperatingSystem.IsWindows()) return null;
    var title = pickFile ? "Select binary" : "Select folder";
    var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        try { tcs.TrySetResult(ShowExplorerPicker(pickFile, title)); }
        catch (Exception ex) { tcs.TrySetException(ex); }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();
    try
    {
        return await tcs.Task.WaitAsync(TimeSpan.FromMinutes(5));
    }
    catch (TimeoutException)
    {
        return null;
    }
}

static string? ShowExplorerPicker(bool pickFile, string title)
{
    var dlg = (IFileOpenDialog)new FileOpenDialog();
    const uint ForceFilesystem = 0x40;
    const uint PathMustExist = 0x800;
    const uint FileMustExist = 0x1000;
    const uint PickFolders = 0x20;
    const uint SigdnFileSysPath = 0x80058000;
    var opts = ForceFilesystem | PathMustExist;
    opts |= pickFile ? FileMustExist : PickFolders;
    dlg.SetOptions(opts);
    dlg.SetTitle(title);

    var forced = 0;
    using var timer = new Timer(_ =>
    {
        if (Interlocked.Exchange(ref forced, 1) == 1) return;
        var h = WindowForeground.FindByTitle(title);
        if (h == IntPtr.Zero)
        {
            Interlocked.Exchange(ref forced, 0);
            return;
        }
        WindowForeground.ForceForeground(h);
    }, null, 200, 200);

    var hr = dlg.Show(IntPtr.Zero);
    timer.Change(Timeout.Infinite, Timeout.Infinite);
    if (hr != 0) return null;
    dlg.GetResult(out var item);
    item.GetDisplayName(SigdnFileSysPath, out var path);
    return string.IsNullOrWhiteSpace(path) ? null : path;
}

static class WindowForeground
{
    const uint SwpNoSize = 0x0001;
    const uint SwpNoMove = 0x0002;
    static readonly IntPtr HwndTopmost = new(-1);
    static readonly IntPtr HwndNotTopmost = new(-2);

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    delegate bool EnumProc(IntPtr h, IntPtr l);

    public static IntPtr FindByTitle(string part)
    {
        var found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, 512);
            if (sb.ToString().Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static void ForceForeground(IntPtr h)
    {
        var fg = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var cur = GetCurrentThreadId();
        AttachThreadInput(cur, fgThread, true);
        SetWindowPos(h, HwndTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove);
        SetForegroundWindow(h);
        SetWindowPos(h, HwndNotTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoMove);
        AttachThreadInput(cur, fgThread, false);
    }
}

[ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
class FileOpenDialog;

[ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IFileOpenDialog
{
    [PreserveSig] int Show(IntPtr parent);
    void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
    void SetFileTypeIndex(uint iFileType);
    void GetFileTypeIndex(out uint piFileType);
    void Advise(IntPtr pfde, out uint pdwCookie);
    void Unadvise(uint dwCookie);
    void SetOptions(uint fos);
    void GetOptions(out uint pfos);
    void SetDefaultFolder(IntPtr psi);
    void SetFolder(IntPtr psi);
    void GetFolder(out IntPtr ppsi);
    void GetCurrentSelection(out IntPtr ppsi);
    void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
    void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
    void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
    void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
    void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
    void GetResult(out IShellItem ppsi);
    void AddPlace(IntPtr psi, int alignment);
    void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
    void Close(int hr);
    void SetClientGuid(ref Guid guid);
    void ClearClientData();
    void SetFilter(IntPtr pFilter);
    void GetResults(out IntPtr ppenum);
    void GetSelectedItems(out IntPtr ppsai);
}

[ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellItem
{
    void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    void GetParent(out IShellItem ppsi);
    void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
    void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    void Compare(IShellItem psi, uint hint, out int piOrder);
}

static class BrowseGate
{
    public static readonly SemaphoreSlim Lock = new(1, 1);
}

sealed class EventHub : ILiveEventListener
{
    readonly List<Channel<(string Name, object Payload)>> _subs = [];
    readonly object _listenerLock = new();

    public void Emit(string eventName, object payload)
    {
        Channel<(string, object)>[] copy;
        lock (_listenerLock) copy = _subs.ToArray();
        foreach (var ch in copy)
            ch.Writer.TryWrite((eventName, payload));
    }

    public async IAsyncEnumerable<(string Name, object Payload)> Subscribe([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var ch = Channel.CreateBounded<(string, object)>(new BoundedChannelOptions(200)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        lock (_listenerLock) _subs.Add(ch);
        try
        {
            await foreach (var item in ch.Reader.ReadAllAsync(ct))
                yield return item;
        }
        finally
        {
            lock (_listenerLock) _subs.Remove(ch);
        }
    }
}

sealed record SettingsRequest(
    string? InstallDir,
    string? ToolDir,
    string? Username,
    bool? CreateIfMissing);

sealed record LoginRequest(string? Username, string? Password, bool Qr);
sealed record GuardRequest(string? Code);
sealed record SwitchRequest(string? PatchId, string? ManifestId, bool Confirm);
sealed record OpenRequest(string? Which);
sealed record SetupToolRequest(string? Path);
sealed record BrowseRequest(string? Kind);
