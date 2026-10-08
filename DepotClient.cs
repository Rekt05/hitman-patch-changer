using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace HitmanPatchChanger;

public enum JobKind { None, Login, Switch }

public enum PromptKind { None, Password, Guard, Email, Mobile, Qr }

public enum ToolStatus { Missing, Downloading, Ready, Failed }

public sealed class SessionSnapshot
{
    public string Phase { get; set; } = "idle";
    public JobKind Job { get; set; }
    public PromptKind Prompt { get; set; }
    public string? PromptDetail { get; set; }
    public string? QrAscii { get; set; }
    public bool[][]? QrModules { get; set; }
    public string? LastError { get; set; }
    public string? LastErrorCode { get; set; }
    public string? Percent { get; set; }
    public string? LastLine { get; set; }
    public bool Busy { get; set; }
    public bool Success { get; set; }
    public string? CommandPreview { get; set; }
    public string? ResumeVersion { get; set; }

    public SessionSnapshot Clone() => (SessionSnapshot)MemberwiseClone();
}

public sealed class UiState
{
    public AppConfig Config { get; set; } = new();
    public string? DetectedVersion { get; set; }
    public bool CachePresent { get; set; }
    public bool InstallExists { get; set; }
    public bool InstallCompatible { get; set; }
    public bool ToolDirExists { get; set; }
    public bool SteamAppIdPresent { get; set; }
    public bool AccountConfigPresent { get; set; }
    public bool SessionLikelyValid { get; set; }
    public ToolStatus DepotDownloader { get; set; }
    public string? DepotDownloaderPath { get; set; }
    public string? DepotDownloaderVersion { get; set; }
    public string? ToolPath { get; set; }
    public SessionSnapshot Session { get; set; } = new();
    public string ListenUrl { get; set; } = "http://127.0.0.1:3847";
    public string Platform { get; set; } = "";
}

public sealed class LogLine
{
    public string Stream { get; set; } = "stdout";
    public string Text { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
}

public sealed class LogBuffer
{
    readonly ConcurrentQueue<LogLine> _log = new();
    readonly ILiveEventListener _liveEvents;

    public LogBuffer(ILiveEventListener liveEvents) => _liveEvents = liveEvents;

    public IReadOnlyCollection<LogLine> Recent => _log.ToArray();

    public void Append(string stream, string text)
    {
        var line = new LogLine { Stream = stream, Text = text };
        _log.Enqueue(line);
        while (_log.Count > 2000 && _log.TryDequeue(out _)) { }
        _liveEvents.Emit("log", line);
    }
}

public interface ILiveEventListener
{
    void Emit(string eventName, object payload);
}

public sealed class DepotClient : IDisposable
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    static readonly Regex PercentRegex = new(@"(\d+(?:\.\d+)?)\s*%", RegexOptions.Compiled);

    readonly object _sessionLock = new();
    readonly LogBuffer _logs;
    readonly ILiveEventListener _liveEvents;
    readonly int _steamLoginId;
    Process? _proc;
    StringBuilder _pending = new();
    StringBuilder _qr = new();
    bool _capturingQr;
    string? _pendingPassword;
    CancellationTokenSource? _runCts;
    bool _userCancel;
    bool _sawCdnTrouble;
    bool _betweenLoginAndSwitch;
    int _jobGeneration;
    int _thisRunGeneration;
    Task _jobTask = Task.CompletedTask;
    PatchEntry? _activePatch;
    PatchEntry? _resumePatch;
    ToolStatus _toolStatus = ToolStatus.Missing;
    string? _toolPath;
    string? _toolVersion;
    string? _toolError;
    SessionSnapshot _session = new();

    public DepotClient(ILiveEventListener liveEvents, LogBuffer logs, int steamLoginId = 38470001)
    {
        _liveEvents = liveEvents;
        _logs = logs;
        _steamLoginId = steamLoginId;
    }

    public SessionSnapshot Snapshot()
    {
        lock (_sessionLock) return _session.Clone();
    }

    public bool Busy
    {
        get { lock (_sessionLock) return _session.Busy; }
    }

    public bool IsTransferring
    {
        get { lock (_sessionLock) return IsTransferringUnlocked(); }
    }

    public bool IsLoggingIn
    {
        get { lock (_sessionLock) return _betweenLoginAndSwitch && _session.Job == JobKind.Login; }
    }

    public void StoreRunningTask(Task job)
    {
        lock (_sessionLock) _jobTask = job;
    }

    public void KeepSwitchForResume()
    {
        string? label = null;
        lock (_sessionLock)
        {
            if (_session.Job != JobKind.Switch || !_session.Busy || !IsAuthPrompt(_session.Prompt) || _activePatch == null)
                return;
            _resumePatch = _activePatch;
            label = PatchLabel(_activePatch);
            _session.ResumeVersion = label;
        }
        if (label != null)
            AppendLog("sys", $"Sign-in will continue the switch to {label}.");
    }

    public void QueueResume(PatchEntry patch)
    {
        var label = PatchLabel(patch);
        lock (_sessionLock)
        {
            _resumePatch = patch;
            _session.ResumeVersion = label;
            if (_session.Prompt == PromptKind.Qr)
                _session.PromptDetail = QrDetailUnlocked();
        }
        AppendLog("sys", $"After sign-in, switching to {label}.");
    }

    public Task PrepareReplace(bool keepResume, bool allowLoginJob)
    {
        Process? proc;
        Task job;
        lock (_sessionLock)
        {
            if (!keepResume)
            {
                _resumePatch = null;
                _session.ResumeVersion = null;
            }
            var auth = _session.Busy && IsAuthPrompt(_session.Prompt);
            var login = allowLoginJob && _session.Job == JobKind.Login && (_session.Busy || _betweenLoginAndSwitch);
            if (IsTransferringUnlocked() || (!auth && !login))
                return Task.CompletedTask;
            _jobGeneration++;
            _userCancel = true;
            job = _jobTask;
            proc = _proc;
            _runCts?.Cancel();
        }
        try { proc?.Kill(entireProcessTree: true); } catch { }
        return job;
    }

    bool IsTransferringUnlocked()
        => _session.Busy && _session.Job == JobKind.Switch && !IsAuthPrompt(_session.Prompt);

    static bool IsAuthPrompt(PromptKind kind)
        => kind is PromptKind.Password or PromptKind.Guard or PromptKind.Email or PromptKind.Mobile or PromptKind.Qr;

    static string PatchLabel(PatchEntry patch)
        => string.IsNullOrWhiteSpace(patch.Id) ? patch.Version : patch.Id;

    string QrDetailUnlocked()
    {
        var detail = "Scan the qr code using the steam app on your phone";
        if (_resumePatch != null)
            detail += $" After sign-in, switching to {PatchLabel(_resumePatch)}.";
        return detail;
    }

    string SignInHintUnlocked()
    {
        var hint = "Use Log in, or Log in via QR code.";
        var patch = _resumePatch ?? (_session.Job == JobKind.Switch ? _activePatch : null);
        if (patch != null)
            hint += $" The switch to {PatchLabel(patch)} continues after you sign in.";
        return hint;
    }

    public ToolStatus ToolsStatus => _toolStatus;
    public string? ToolsPath => _toolPath;
    public string? ToolsVersion => _toolVersion;
    public string? ToolsError => _toolError;

    static string? _accountConfigPath;
    static DateTime _accountConfigCheck;

    public bool AccountConfigPresent
    {
        get
        {
            if (DateTime.UtcNow - _accountConfigCheck < TimeSpan.FromSeconds(5))
                return _accountConfigPath != null;
            _accountConfigCheck = DateTime.UtcNow;
            _accountConfigPath = FindAccountConfig();
            return _accountConfigPath != null;
        }
    }

    static IEnumerable<string> AccountConfigRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (!string.IsNullOrWhiteSpace(local))
            yield return Path.Combine(local, "IsolatedStorage");
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (!string.IsNullOrWhiteSpace(roaming))
            yield return Path.Combine(roaming, ".isolated-storage");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        if (!string.IsNullOrWhiteSpace(home))
            yield return Path.Combine(home, ".isolated-storage");
    }

    static string? FindAccountConfig()
    {
        foreach (var root in AccountConfigRoots())
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var hit = Directory.EnumerateFiles(root, "account.config", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (hit != null) return hit;
            }
            catch { }
        }
        return null;
    }

    public void UseToolDir(string path)
    {
        path = Paths.ExpandHome(path);
        if (path.Length == 0) throw new InvalidOperationException("Choose a DepotDownloader directory first.");
        string dir;
        if (File.Exists(path)) dir = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidOperationException("Could not read that path.");
        else dir = path;
        Directory.CreateDirectory(dir);
        var cfg = ConfigStore.Load();
        cfg.ToolDir = dir;
        ConfigStore.Save(cfg);
        if (File.Exists(path) && !string.Equals(Path.GetFullPath(path), Path.Combine(dir, Path.GetFileName(path)), StringComparison.OrdinalIgnoreCase))
        {
            var destName = OperatingSystem.IsWindows()
                ? "DepotDownloader.exe"
                : (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "DepotDownloader.dll" : "DepotDownloader");
            File.Copy(path, Path.Combine(dir, destName), overwrite: true);
        }
        if (!ProbeBinary())
        {
            _toolStatus = ToolStatus.Missing;
            _toolPath = null;
            _toolVersion = null;
            BroadcastState();
        }
    }

    public async Task EnsureBinaryAsync(CancellationToken ct = default)
    {
        var home = ToolHome() ?? throw new InvalidOperationException("Choose a DepotDownloader directory first.");
        Directory.CreateDirectory(home);
        var exe = FindLocalBinary(home);
        if (exe != null)
        {
            SetReady(exe);
            return;
        }

        _toolStatus = ToolStatus.Downloading;
        BroadcastState();
        try
        {
            var zipName = ZipNameForHost();
            var url = $"https://github.com/SteamRE/DepotDownloader/releases/latest/download/{zipName}";
            AppendLog("sys", $"Downloading {zipName} from GitHub releases...");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("HitmanPatchChanger", "1.0"));
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var zipPath = Path.Combine(home, zipName);
            await using (var fs = File.Create(zipPath))
                await resp.Content.CopyToAsync(fs, ct);
            ZipFile.ExtractToDirectory(zipPath, home, overwriteFiles: true);
            File.Delete(zipPath);
            exe = FindLocalBinary(home) ?? throw new FileNotFoundException("DepotDownloader was downloaded but the binary was not found.");
            SetReady(exe);
            AppendLog("sys", $"DepotDownloader ready: {exe}");
        }
        catch (Exception ex)
        {
            _toolStatus = ToolStatus.Failed;
            _toolError = ex.Message;
            AppendLog("sys", "Failed to fetch DepotDownloader: " + ex.Message);
            BroadcastState();
        }
    }

    public async Task LoginAsync(AppConfig cfg, string? username, string? password, bool qr)
    {
        if (!qr && string.IsNullOrWhiteSpace(username))
            throw new InvalidOperationException("Enter a Steam username, or use Log in via QR code.");

        if (!ProbeBinary())
            throw new InvalidOperationException(_toolError ?? "Choose a DepotDownloader directory and download it first.");

        int jobGeneration;
        lock (_sessionLock)
        {
            jobGeneration = _jobGeneration;
            _betweenLoginAndSwitch = true;
            _userCancel = false;
        }

        try
        {
            var args = new List<string>
            {
                "-app", cfg.AppId.ToString(),
                "-depot", cfg.DepotId.ToString(),
                "-manifest-only",
                "-remember-password",
                "-dir", Paths.LoginScratch,
                "-loginid", _steamLoginId.ToString()
            };
            if (qr) args.Add("-qr");
            else
            {
                args.Add("-username");
                args.Add(username!.Trim());
            }

            Directory.CreateDirectory(Paths.LoginScratch);
            await RunAsync(JobKind.Login, args, password, jobGeneration, onSuccess: () =>
            {
                if (!string.IsNullOrWhiteSpace(username))
                {
                    cfg.Username = username.Trim();
                    ConfigStore.Save(cfg);
                }
            });
            if (FinishIfCancelled(jobGeneration) || !Snapshot().Success) return;

            PatchEntry? next;
            lock (_sessionLock)
            {
                next = _resumePatch;
                _resumePatch = null;
                _session.ResumeVersion = null;
            }
            if (next == null) return;
            AppendLog("sys", $"Signed in. Starting switch to {PatchLabel(next)}.");
            await SwitchAsync(ConfigStore.Load(), next);
        }
        finally
        {
            lock (_sessionLock) _betweenLoginAndSwitch = false;
        }
    }

    public async Task SwitchAsync(AppConfig cfg, PatchEntry patch)
    {
        if (string.IsNullOrWhiteSpace(cfg.InstallDir))
            throw new InvalidOperationException("Set an install directory first.");
        if (string.IsNullOrWhiteSpace(cfg.Username))
            throw new InvalidOperationException("Log in to Steam first so a username is saved.");

        if (!ProbeBinary())
            throw new InvalidOperationException(_toolError ?? "Choose a DepotDownloader directory and download it first.");

        Directory.CreateDirectory(cfg.InstallDir);

        int jobGeneration;
        lock (_sessionLock)
        {
            jobGeneration = _jobGeneration;
            _activePatch = patch;
            _userCancel = false;
        }
        var requested = ClampDownloads(cfg.MaxDownloads);
        const int maxAttempts = 4;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (FinishIfCancelled(jobGeneration)) return;
            var downloads = attempt == 1 ? requested : Math.Max(2, requested / (1 << (attempt - 1)));
            if (attempt > 1)
            {
                var wait = TimeSpan.FromSeconds(10 * attempt);
                lock (_sessionLock)
                {
                    _session.Busy = true;
                    _session.Phase = "retrying";
                    _session.Success = false;
                    _session.LastErrorCode = "cdn";
                    _session.LastError = $"Steam's CDN is timing out because this is probably an old build that isn't downloaded much. Retrying in {(int)wait.TotalSeconds}s. Leave the process running.";
                }
                AppendLog("sys", $"CDN retry {attempt}/{maxAttempts} after {wait.TotalSeconds:0}s with -max-downloads {downloads}.");
                BroadcastState();
                var until = DateTime.UtcNow + wait;
                while (!_userCancel && DateTime.UtcNow < until)
                    await Task.Delay(250);
                if (FinishIfCancelled(jobGeneration)) return;
            }

            var args = SwitchArgs(cfg, patch, downloads);
            await RunAsync(JobKind.Switch, args, password: null, jobGeneration, onSuccess: () =>
            {
                WriteSteamAppId(cfg);
                cfg.LastVersion = patch.Version;
                cfg.LastManifestId = patch.SteamManifestId;
                ConfigStore.Save(cfg);
            });

            if (FinishIfCancelled(jobGeneration)) return;
            var snap = Snapshot();
            if (snap.Success) return;
            if (!_sawCdnTrouble) return;
            if (attempt == maxAttempts)
            {
                lock (_sessionLock)
                {
                    _session.LastErrorCode = "cdn";
                    _session.LastError = "Steam's CDN kept timing out because this is probably an old build that isn't downloaded much. Leave it a while, then click Switch again.";
                }
                BroadcastState();
            }
        }
    }

    bool FinishIfCancelled(int jobGeneration)
    {
        lock (_sessionLock)
        {
            if (jobGeneration != _jobGeneration) return true;
            if (!_userCancel) return false;
            _session.Busy = false;
            _session.Success = false;
            _session.Phase = "failed";
            _session.Prompt = PromptKind.None;
            _session.LastErrorCode = "cancelled";
            _session.LastError = "Cancelled.";
            _session.ResumeVersion = null;
        }
        BroadcastState();
        return true;
    }

    List<string> SwitchArgs(AppConfig cfg, PatchEntry patch, int maxDownloads)
    {
        var args = new List<string>
        {
            "-app", cfg.AppId.ToString(),
            "-depot", cfg.DepotId.ToString(),
            "-manifest", patch.SteamManifestId,
            "-username", cfg.Username.Trim(),
            "-remember-password",
            "-dir", cfg.InstallDir,
            "-validate",
            "-loginid", _steamLoginId.ToString(),
            "-max-downloads", maxDownloads.ToString()
        };
        if (cfg.CellId is > 0)
        {
            args.Add("-cellid");
            args.Add(cfg.CellId.Value.ToString());
        }
        return args;
    }

    static int ClampDownloads(int value) => value is < 1 or > 32 ? 8 : value;

    public bool SubmitInput(string text)
    {
        lock (_sessionLock)
        {
            if (_proc is not { HasExited: false } || _proc.StandardInput is null)
                return false;
            _proc.StandardInput.WriteLine(text.Trim());
            _proc.StandardInput.Flush();
            if (_session.Prompt is PromptKind.Guard or PromptKind.Email or PromptKind.Password)
            {
                _session.Prompt = PromptKind.None;
                _session.PromptDetail = null;
            }
        }
        AppendLog("sys", "Sent response to DepotDownloader.");
        BroadcastState();
        return true;
    }

    public void Cancel()
    {
        Process? p;
        lock (_sessionLock)
        {
            _userCancel = true;
            _resumePatch = null;
            _session.ResumeVersion = null;
            p = _proc;
            _runCts?.Cancel();
        }
        try { p?.Kill(entireProcessTree: true); } catch { }
    }

    public void Fail(string code, string message)
    {
        lock (_sessionLock)
        {
            _session.Busy = false;
            _session.Success = false;
            _session.Phase = "failed";
            _session.Prompt = PromptKind.None;
            _session.LastErrorCode = code;
            _session.LastError = message;
        }
        AppendLog("sys", message);
        BroadcastState();
    }

    public void Dispose()
    {
        Cancel();
        _proc?.Dispose();
    }

    async Task RunAsync(JobKind job, List<string> args, string? password, int jobGeneration, Action onSuccess)
    {
        lock (_sessionLock)
        {
            if (jobGeneration != _jobGeneration) return;
            if (_proc is { HasExited: false })
                throw new InvalidOperationException("A DepotDownloader job is already running.");
            _thisRunGeneration = jobGeneration;
            _pendingPassword = password;
            _pending.Clear();
            _qr.Clear();
            _capturingQr = false;
            _sawCdnTrouble = false;
            _session = new SessionSnapshot
            {
                Phase = job == JobKind.Login ? "logging-in" : "switching",
                Job = job,
                Busy = true,
                CommandPreview = Redact(args),
                ResumeVersion = _resumePatch == null ? null : PatchLabel(_resumePatch)
            };
        }
        BroadcastState();

        var home = ToolHome() ?? throw new InvalidOperationException("Choose a DepotDownloader directory first.");
        Directory.CreateDirectory(home);
        EnsureExecutable(_toolPath!);
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = home,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        SetToolCommand(psi, _toolPath!);
        foreach (var a in args) psi.ArgumentList.Add(a);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var cts = new CancellationTokenSource();
        lock (_sessionLock)
        {
            _proc = proc;
            _runCts = cts;
        }

        AppendLog("sys", "Starting: " + Redact(args));

        if (!proc.Start())
            throw new InvalidOperationException("Failed to start DepotDownloader.");

        var stdout = Pump(proc.StandardOutput, "stdout", cts.Token);
        var stderr = Pump(proc.StandardError, "stderr", cts.Token);
        await proc.WaitForExitAsync();
        cts.Cancel();
        try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }

        var code = proc.ExitCode;
        var superseded = false;
        lock (_sessionLock)
        {
            if (ReferenceEquals(_proc, proc))
            {
                _proc = null;
                _pendingPassword = null;
            }
            if (jobGeneration != _jobGeneration)
            {
                superseded = true;
            }
            else
            {
                _session.Busy = false;
                _session.Success = code == 0;
                _session.Phase = code == 0 ? "done" : "failed";
                _session.Prompt = PromptKind.None;
                if (code == 0)
                {
                    _session.LastError = null;
                    _session.LastErrorCode = null;
                }
                else if (string.IsNullOrEmpty(_session.LastError))
                    _session.LastError = $"DepotDownloader exited with code {code}.";
            }
        }

        if (superseded) return;
        try
        {
            if (code == 0) onSuccess();
            else AppendLog("sys", $"DepotDownloader exited with code {code}.");
        }
        finally
        {
            BroadcastState();
        }
    }

    async Task Pump(StreamReader reader, string stream, CancellationToken ct)
    {
        var buf = new char[2048];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await reader.ReadAsync(buf.AsMemory(0, buf.Length), ct);
                if (n <= 0) break;
                HandleChunk(stream, buf.AsSpan(0, n).ToString());
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    void HandleChunk(string stream, string chunk)
    {
        lock (_sessionLock)
        {
            if (_thisRunGeneration != _jobGeneration) return;
            _pending.Append(chunk);
            var text = _pending.ToString();
            var parts = text.Split('\n');
            _pending.Clear();
            if (!text.EndsWith('\n'))
            {
                _pending.Append(parts[^1]);
                parts = parts[..^1];
            }
            foreach (var raw in parts)
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 && !_capturingQr) continue;
                IngestLine(stream, line);
            }
            if (_pending.Length > 0)
            {
                var promptBefore = _session.Prompt;
                MaybePrompt(_pending.ToString());
                if (_session.Prompt != promptBefore)
                    BroadcastState();
            }
        }
    }

    void IngestLine(string stream, string line)
    {
        if (!IsQrArt(line))
            AppendLogUnlocked(stream, line);
        _session.LastLine = line;
        var m = PercentRegex.Match(line);
        if (m.Success) _session.Percent = m.Groups[1].Value;

        if (line.Contains("Use the Steam Mobile App to sign in with this QR code", StringComparison.OrdinalIgnoreCase)
            || line.Contains("The QR code has changed", StringComparison.OrdinalIgnoreCase))
        {
            _capturingQr = true;
            _qr.Clear();
            _session.Prompt = PromptKind.Qr;
            _session.Phase = "qr";
            _session.PromptDetail = QrDetailUnlocked();
            _session.QrModules = null;
            _session.QrAscii = null;
        }
        else if (_capturingQr)
        {
            if (IsQrArt(line) || line.Length == 0)
            {
                _qr.AppendLine(line);
                _session.QrAscii = _qr.ToString();
                _session.QrModules = ParseQrModules(_session.QrAscii);
            }
            else
            {
                _capturingQr = false;
            }
        }

        MaybePrompt(line);
        ClassifyError(line);

        if (line.Contains("Success! Next time you can login with -username", StringComparison.OrdinalIgnoreCase))
        {
            var idx = line.IndexOf("-username ", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var rest = line[(idx + "-username ".Length)..];
                var name = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var cfg = ConfigStore.Load();
                    cfg.Username = name;
                    ConfigStore.Save(cfg);
                }
            }
        }

        BroadcastState();
    }

    void MaybePrompt(string text)
    {
        if (text.Contains("Enter account password for", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(_pendingPassword) && _proc?.StandardInput != null)
            {
                _proc.StandardInput.WriteLine(_pendingPassword);
                _proc.StandardInput.Flush();
                _pendingPassword = null;
                AppendLogUnlocked("sys", "Password supplied to DepotDownloader.");
            }
            else if (_session.Prompt != PromptKind.Password)
            {
                _session.Prompt = PromptKind.Password;
                _session.Phase = "password";
                _session.LastError = null;
                _session.LastErrorCode = null;
                _session.PromptDetail = SignInHintUnlocked();
                AppendLogUnlocked("sys", SignInHintUnlocked());
            }
            return;
        }

        if (text.Contains("Steam Guard, use the Steam Mobile App to confirm", StringComparison.OrdinalIgnoreCase))
        {
            _session.Prompt = PromptKind.Mobile;
            _session.Phase = "mobile";
            _session.PromptDetail = "Approve the sign-in in the Steam app. If that prompt does not arrive, cancel and use Log in via QR code.";
            return;
        }

        if (text.Contains("2-factor auth code from your authenticator", StringComparison.OrdinalIgnoreCase)
            || text.Contains("2 factor auth code from your authenticator", StringComparison.OrdinalIgnoreCase)
            || text.Contains("auth code sent to the email", StringComparison.OrdinalIgnoreCase)
            || text.Contains("authentication code sent to your email", StringComparison.OrdinalIgnoreCase))
        {
            _session.Prompt = PromptKind.Guard;
            _session.Phase = "guard";
            _session.PromptDetail = SignInHintUnlocked();
            _session.LastError = null;
            _session.LastErrorCode = null;
            return;
        }
    }

    void ClassifyError(string line)
    {
        if (line.Contains("No manifest request code", StringComparison.OrdinalIgnoreCase)
            || (line.Contains("401", StringComparison.Ordinal) && line.Contains("manifest", StringComparison.OrdinalIgnoreCase)))
        {
            _session.LastErrorCode = "manifest-denied";
            _session.LastError = "Steam refused a manifest request code for this build (often a 401). That is a Steam-side policy on historical depots - this app cannot bypass it. Try a newer patch, or retry later.";
        }
        else if (IsCdnChunkLine(line))
        {
            _sawCdnTrouble = true;
            if (_session.LastErrorCode is null or "cdn")
            {
                _session.LastErrorCode = "cdn";
                _session.LastError = "Steam's CDN is timing out because this is probably an old build that isn't downloaded much. Leave the process running. If it stops, click Switch again.";
            }
        }
        else if (line.Contains("RateLimitExceeded", StringComparison.OrdinalIgnoreCase)
                 || line.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            _session.LastErrorCode = "rate-limit";
            _session.LastError = "Steam rate-limited the login. Wait before trying again, do not spam attempts.";
        }
        else if (line.Contains("Access token was rejected", StringComparison.OrdinalIgnoreCase)
                 || line.Contains("InvalidPassword", StringComparison.OrdinalIgnoreCase)
                 || line.Contains("Unable to login to Steam3", StringComparison.OrdinalIgnoreCase))
        {
            _session.LastErrorCode = "login-failed";
            _session.LastError = line.Trim();
        }
        else if (line.Contains("There is not enough space", StringComparison.OrdinalIgnoreCase)
                 || line.Contains("disk full", StringComparison.OrdinalIgnoreCase)
                 || line.Contains("HRESULT: 0x80070070", StringComparison.OrdinalIgnoreCase))
        {
            _session.LastErrorCode = "disk-full";
            _session.LastError = "The disk ran out of free space while switching. Switching needs extra space while files are rebuilt, then settles back to one install size.";
        }
        else if (line.Contains("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase)
                 || line.Contains("Access to the path", StringComparison.OrdinalIgnoreCase)
                 || line.Contains("permission", StringComparison.OrdinalIgnoreCase))
        {
            _session.LastErrorCode = "permission";
            _session.LastError = "Permission denied writing the install folder. Pick a directory your user can write to, and close the game if it is running.";
        }
        else if (line.Contains("AsyncJobFailedException", StringComparison.Ordinal))
        {
            _session.LastErrorCode = "login-failed";
            _session.LastError = "The QR code login timed out. Click Log in via QR code again.";
        }
        else if (line.Contains("Failed to authenticate with Steam", StringComparison.OrdinalIgnoreCase)
                 || line.Contains("previous 2-factor auth code", StringComparison.OrdinalIgnoreCase))
        {
            _session.LastErrorCode = "login-failed";
            _session.LastError = line.Trim();
        }
    }

    void AppendLog(string stream, string text)
    {
        lock (_sessionLock) AppendLogUnlocked(stream, text);
        BroadcastState();
    }

    void AppendLogUnlocked(string stream, string text)
    {
        if (LooksSecret(text)) text = "[redacted]";
        _logs.Append(stream, text);
    }

    void BroadcastState() => _liveEvents.Emit("state", Snapshot());

    void SetReady(string exe)
    {
        if (!TryLaunch(exe, out var version))
        {
            _toolPath = exe;
            _toolStatus = ToolStatus.Failed;
            _toolVersion = null;
            _toolError = "DepotDownloader did not start.";
            BroadcastState();
            return;
        }
        _toolPath = exe;
        _toolStatus = ToolStatus.Ready;
        _toolError = null;
        _toolVersion = version;
        BroadcastState();
    }

    static bool TryLaunch(string exe, out string? version)
    {
        version = null;
        EnsureExecutable(exe);
        try
        {
            var psi = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
            };
            SetToolCommand(psi, exe);
            psi.ArgumentList.Add("-V");
            using var p = Process.Start(psi);
            if (p == null) return false;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            var output = (stdout.Result + stderr.Result).Trim();
            if (p.ExitCode != 0 || output.Length == 0) return false;
            version = output.Split('\n').FirstOrDefault()?.Trim();
            return !string.IsNullOrWhiteSpace(version);
        }
        catch { return false; }
    }

    public bool ProbeBinary()
    {
        var exe = FindLocalBinary(ToolHome());
        if (exe == null)
        {
            _toolStatus = ToolStatus.Missing;
            _toolPath = null;
            _toolVersion = null;
            _toolError = null;
            return false;
        }
        SetReady(exe);
        return _toolStatus == ToolStatus.Ready;
    }

    public string? ToolBlockReason()
    {
        var dir = ToolHome();
        if (string.IsNullOrWhiteSpace(dir))
            return "Choose a DepotDownloader folder first.";
        if (!Directory.Exists(dir) || FindLocalBinary(dir) == null)
            return null;
        if (!ProbeBinary())
            return OperatingSystem.IsLinux()
                ? "DepotDownloader did not start. Click Install to download the Linux build, or point this at a folder that already contains that binary."
                : "DepotDownloader did not start. Install the .NET runtime, or download it again.";
        return null;
    }

    public bool BinaryPresent => FindLocalBinary(ToolHome()) != null;

    static string? ToolHome()
    {
        var dir = ConfigStore.Load().ToolDir;
        return string.IsNullOrWhiteSpace(dir) ? null : dir.Trim();
    }

    static void SetToolCommand(ProcessStartInfo psi, string toolPath)
    {
        if (!OperatingSystem.IsWindows() && toolPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "dotnet";
            psi.ArgumentList.Add(toolPath);
            return;
        }
        psi.FileName = toolPath;
    }

    static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            const UnixFileMode exec = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if ((mode & UnixFileMode.UserExecute) != 0) return;
            File.SetUnixFileMode(path, mode | exec);
        }
        catch { }
    }

    static string? FindLocalBinary(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
        var names = OperatingSystem.IsWindows()
            ? new[] { "DepotDownloader.exe" }
            : new[] { "DepotDownloader", "DepotDownloader.dll" };
        foreach (var n in names)
        {
            var p = Path.Combine(dir, n);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    static string ZipNameForHost()
    {
        if (OperatingSystem.IsWindows())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "DepotDownloader-windows-arm64.zip"
                : "DepotDownloader-windows-x64.zip";
        if (OperatingSystem.IsLinux())
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "DepotDownloader-linux-arm64.zip",
                Architecture.Arm => "DepotDownloader-linux-arm.zip",
                _ => "DepotDownloader-linux-x64.zip"
            };
        if (OperatingSystem.IsMacOS())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "DepotDownloader-macos-arm64.zip"
                : "DepotDownloader-macos-x64.zip";
        throw new PlatformNotSupportedException(RuntimeInformation.OSDescription);
    }

    static void WriteSteamAppId(AppConfig cfg)
    {
        var retail = Path.Combine(cfg.InstallDir, "Retail");
        Directory.CreateDirectory(retail);
        var path = Path.Combine(retail, "steam_appid.txt");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing == cfg.SteamAppId.ToString()) return;
        }
        File.WriteAllText(path, cfg.SteamAppId.ToString() + "\n");
    }

    static bool IsQrArt(string line)
    {
        if (line.Length < 8) return false;
        var blocks = line.Count(IsQrChar);
        return blocks >= line.Length * 0.8;
    }

    static bool IsQrChar(char c) =>
        c is '█' or '▄' or '▀' or ' ' or '?' or '░' or '▒' or '▓' or '▌' or '▐' or '■' or '\uFFFD' or '\u00DB';

    static bool IsDarkQrChar(char c) =>
        c is '█' or '▄' or '▀' or '?' or '░' or '▒' or '▓' or '■' or '\uFFFD' or '\u00DB';

    static bool[][]? ParseQrModules(string ascii)
    {
        var lines = ascii.Replace("\r", "").Split('\n')
            .Where(l => l.Length > 0)
            .ToArray();
        if (lines.Length < 15) return null;

        var width = lines.Max(l => l.Length);
        var charsPerCell = 2;
        var cols = width / charsPerCell;
        if (cols < 21) { charsPerCell = 1; cols = width; }
        if (cols < 21) return null;

        var grid = new bool[lines.Length][];
        for (var y = 0; y < lines.Length; y++)
        {
            var line = lines[y].PadRight(width);
            var row = new bool[cols];
            for (var x = 0; x < cols; x++)
                row[x] = IsDarkQrChar(line[x * charsPerCell]);
            grid[y] = row;
        }
        return grid;
    }

    static bool IsCdnChunkLine(string line) =>
        line.Contains("Connection timeout downloading chunk", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Connection timeout downloading depot manifest", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Failed to find any server with chunk", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Error while copying content to a stream", StringComparison.OrdinalIgnoreCase)
        || (line.Contains("downloading chunk", StringComparison.OrdinalIgnoreCase)
            && (line.Contains("ServiceUnavailable", StringComparison.OrdinalIgnoreCase)
                || line.Contains("BadGateway", StringComparison.OrdinalIgnoreCase)
                || line.Contains("NotFound", StringComparison.OrdinalIgnoreCase)
                || line.Contains("GatewayTimeout", StringComparison.OrdinalIgnoreCase)
                || line.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase)))
        || (line.Contains("depot manifest", StringComparison.OrdinalIgnoreCase)
            && (line.Contains("ServiceUnavailable", StringComparison.OrdinalIgnoreCase)
                || line.Contains("BadGateway", StringComparison.OrdinalIgnoreCase)
                || line.Contains("GatewayTimeout", StringComparison.OrdinalIgnoreCase)));

    static bool LooksSecret(string text)
    {
        var scrubbed = text.Replace("-remember-password", "", StringComparison.OrdinalIgnoreCase);
        return ContainsArg(scrubbed, "-password") || ContainsArg(scrubbed, "-pass");
    }

    static bool ContainsArg(string text, string arg)
    {
        var i = 0;
        while ((i = text.IndexOf(arg, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = i == 0 || char.IsWhiteSpace(text[i - 1]);
            var after = i + arg.Length >= text.Length || char.IsWhiteSpace(text[i + arg.Length]);
            if (before && after) return true;
            i += arg.Length;
        }
        return false;
    }

    static string Redact(IReadOnlyList<string> args)
    {
        var parts = new List<string> { "DepotDownloader" };
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            parts.Add(a);
            if ((a.Equals("-password", StringComparison.OrdinalIgnoreCase) || a.Equals("-pass", StringComparison.OrdinalIgnoreCase))
                && i + 1 < args.Count)
            {
                parts.Add("***");
                i++;
            }
        }
        return string.Join(' ', parts);
    }

}

public static class InstallProbe
{
    public static bool CachePresent(string installDir)
        => Directory.Exists(Path.Combine(installDir, ".DepotDownloader"));

    public static bool Compatible(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir)) return false;
        if (Directory.Exists(Path.Combine(installDir, "Retail"))
            && Directory.Exists(Path.Combine(installDir, "Runtime"))
            && File.Exists(Path.Combine(installDir, "Launcher.exe")))
            return true;
        if (CachePresent(installDir)) return true;
        try { return !Directory.EnumerateFileSystemEntries(installDir).Any(); }
        catch { return false; }
    }

    public static string? BlockReason(string? installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
            return "Set an install directory first.";
        return null;
    }

    public static bool SteamAppIdPresent(string installDir)
        => File.Exists(Path.Combine(installDir, "Retail", "steam_appid.txt"));

    public static string? DetectVersion(AppConfig cfg, PatchFile catalog)
    {
        if (!string.IsNullOrWhiteSpace(cfg.LastVersion)) return cfg.LastVersion;
        if (string.IsNullOrWhiteSpace(cfg.InstallDir) || !CachePresent(cfg.InstallDir)) return null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(cfg.InstallDir, ".DepotDownloader")))
            {
                var name = Path.GetFileName(file);
                foreach (var p in catalog.Patches)
                {
                    if (p.SteamManifestId.Length > 0 && name.Contains(p.SteamManifestId, StringComparison.Ordinal))
                        return p.Version;
                }
            }
        }
        catch { }
        return null;
    }
}
