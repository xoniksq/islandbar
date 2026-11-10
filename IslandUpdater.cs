using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace IslandBar;

static class IslandUpdater
{
    const string Repository = "xoniksq/islandbar";
    const string Asset = "IslandBar-Windows-x64.zip";
    const string DirectoryName = ".islandbar-update";
    const string HelperName = "IslandBar.Updater.exe";
    const string ExeName = "IslandBar.exe";
    const long MaxPackageBytes = 250L * 1024 * 1024;
    const long MaxExecutableBytes = 450L * 1024 * 1024;
    static readonly HttpClient Http = CreateClient();
    static readonly Version CurrentVersion = typeof(IslandUpdater).Assembly.GetName().Version!;
    static string Stage => Path.Combine(Program.Folder, DirectoryName);
    static string StagedExe(string dir) => Path.Combine(dir, "pending.exe");
    static string Manifest(string dir) => Path.Combine(dir, "pending.json");
    static string Rejected(string dir) => Path.Combine(dir, "rejected.txt");
    sealed record Pending(string Version, string Digest);
    sealed record ReleaseAsset(Version Version, Uri Url, long Size, string Digest);

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("IslandBar-Updater/1.0");
        return client;
    }

    static async Task<HttpResponseMessage> GetResponse(HttpClient client, Uri url, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++) {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try {
                var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                int code = (int)response.StatusCode;
                if (attempt != 0 || code is not (408 or 429 or >= 500)) return response;
                response.Dispose();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) {
                if (attempt != 0) throw new TimeoutException("GitHub не ответил вовремя.");
            }
            catch (HttpRequestException) when (attempt == 0 && !token.IsCancellationRequested) { }
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
    }

    static Version? ParseVersion(string? value)
    {
        if (!Version.TryParse(value, out var version) || version.Build < 0) return null;
        return new Version(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision));
    }

    static ReleaseAsset? GetAsset(JsonElement release)
    {
        string? declared = release.GetProperty("version").GetString();
        if (ParseVersion(declared) is not { } version || declared != version.ToString(3)) return null;
        long size = release.GetProperty("size").GetInt64();
        string? digest = release.GetProperty("sha256").GetString();
        if (size <= 0 || size > MaxPackageBytes || !IsDigest(digest)) return null;
        return new(version, new Uri($"https://github.com/{Repository}/releases/download/v{declared}/{Asset}"), size, digest!);
    }

    static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    static Pending? ReadPending(string dir, Version installed)
    {
        if (!File.Exists(Manifest(dir)) || !File.Exists(StagedExe(dir))) return null;
        try {
            var pending = JsonSerializer.Deserialize<Pending>(File.ReadAllText(Manifest(dir)));
            return pending is not null && IsDigest(pending.Digest) && ParseVersion(pending.Version) is { } next &&
                next > installed ? pending : null;
        }
        catch (JsonException) { return null; }
    }

    public static async Task<string?> CheckAsync()
    {
        if (ReadPending(Stage, CurrentVersion) is { } pending) return pending.Version;
        using var checkTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(105));
        using var response = await GetResponse(Http, new Uri($"https://github.com/{Repository}/releases/latest/download/islandbar-update.json"), checkTimeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(checkTimeout.Token));
        if (GetAsset(release.RootElement) is not { } asset || asset.Version <= CurrentVersion) return null;
        string version = asset.Version.ToString(3);
        if (File.Exists(Rejected(Stage)) && File.ReadAllText(Rejected(Stage)).Trim() == version) return null;
        Directory.CreateDirectory(Stage);
        string package = Path.Combine(Stage, "package.tmp");
        string executable = Path.Combine(Stage, "exe.tmp");
        try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await Download(asset, package, timeout.Token);
            string digest = await Unpack(package, executable, asset.Version, timeout.Token);
            File.Move(executable, StagedExe(Stage), overwrite: true);
            string manifest = Manifest(Stage) + ".tmp";
            File.WriteAllText(manifest, JsonSerializer.Serialize(new Pending(version, digest)));
            File.Move(manifest, Manifest(Stage), overwrite: true);
            return version;
        }
        finally {
            File.Delete(package);
            File.Delete(executable);
        }
    }

    static async Task Download(ReleaseAsset asset, string path, CancellationToken token)
    {
        using var response = await GetResponse(Http, asset.Url, token);
        response.EnsureSuccessStatusCode();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = File.Create(path);
        byte[] buffer = new byte[64 * 1024];
        long count = 0;
        for (int read; (read = await input.ReadAsync(buffer, token)) != 0;) {
            count += read;
            if (count > asset.Size) throw new IOException("Сборка превышает размер из релиза.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        if (count != asset.Size || !SameDigest(Convert.ToHexString(hash.GetHashAndReset()), asset.Digest))
            throw new IOException("Контрольная сумма сборки не совпала.");
    }

    static async Task<string> Unpack(string package, string output, Version expected, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(package);
        var entry = archive.GetEntry(ExeName);
        if (entry is null || entry.Length <= 0 || entry.Length > MaxExecutableBytes)
            throw new IOException("В релизе нет подходящего файла островка.");
        using (var source = entry.Open())
        await using (var destination = File.Create(output))
            await source.CopyToAsync(destination, token);
        if (FileVersion(output) != expected) throw new IOException("Версия файла не совпадает с релизом.");
        await using var stream = File.OpenRead(output);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    static Version? FileVersion(string path) => ParseVersion(FileVersionInfo.GetVersionInfo(path).FileVersion);
    static bool SameDigest(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);

    public static bool StartHelper()
    {
        string app = Environment.ProcessPath!;
        if (!Path.GetFileName(app).Equals(ExeName, StringComparison.OrdinalIgnoreCase) || ReadPending(Stage, CurrentVersion) is null)
            return false;
        string helper = Path.Combine(Stage, HelperName);
        File.Copy(app, helper, overwrite: true);
        using var started = Start(helper, "--apply-update", Environment.ProcessId.ToString());
        return started is not null;
    }

    public static int Apply(int parentId)
    {
        string dir = Program.Folder;
        if (!string.Equals(Path.GetFileName(dir), DirectoryName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(Environment.ProcessPath), HelperName, StringComparison.OrdinalIgnoreCase)) return 1;
        string root = Path.GetDirectoryName(dir)!;
        string app = Path.Combine(root, ExeName);
        string backup = Path.Combine(dir, "previous.exe");
        bool replaced = false, parentGone = false;
        string? installing = null;
        Process? child = null;
        try {
            using var applyLock = new FileStream(Path.Combine(dir, "apply.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (!WaitForParent(parentId, app)) return 1;
            parentGone = true;
            var installed = FileVersion(app) ?? throw new IOException("Не удалось прочитать установленную версию.");
            var pending = ReadPending(dir, installed) ?? throw new IOException("Подготовленная сборка недоступна.");
            installing = pending.Version;
            string staged = StagedExe(dir);
            string actual;
            using (var stream = File.OpenRead(staged)) actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!SameDigest(actual, pending.Digest) || FileVersion(staged) != ParseVersion(pending.Version))
                throw new IOException("Подготовленная сборка изменилась.");
            ReplaceWithRetry(staged, app, backup);
            replaced = true;
            child = Start(app) ?? throw new IOException("Не удалось запустить островок.");
            if (!WaitForStartup(child, root, pending.Version)) throw new IOException("Новая версия не подтвердила запуск.");
            TryDeleteManifest(dir);
            Program.Log("update installed: " + pending.Version);
            return 0;
        }
        catch (Exception ex) {
            Program.Log("update apply: " + ex.Message);
            if (parentGone) try {
                if (replaced) {
                    if (child is { HasExited: false }) { child.Kill(); child.WaitForExit(4000); }
                    ReplaceWithRetry(backup, app, Path.Combine(dir, "failed.exe"));
                    if (installing is not null) File.WriteAllText(Rejected(dir), installing);
                }
                TryDeleteManifest(dir);
                using var restarted = Start(app);
            }
            catch (Exception recovery) { Program.Log("update recovery: " + recovery.Message); }
            return 1;
        }
        finally { child?.Dispose(); }
    }

    static void TryDeleteManifest(string dir)
    {
        try { File.Delete(Manifest(dir)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("update manifest: " + ex.Message); }
    }

    static bool WaitForParent(int id, string app)
    {
        try {
            using var parent = Process.GetProcessById(id);
            if (parent.HasExited) return true;
            try {
                if (!string.Equals(parent.MainModule?.FileName, app, StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch (InvalidOperationException) when (parent.HasExited) { return true; }
            return parent.WaitForExit(15_000);
        }
        catch (ArgumentException) { return true; }
    }

    static void ReplaceWithRetry(string source, string destination, string backup)
    {
        for (int attempt = 0; ; attempt++) {
            try { File.Replace(source, destination, backup); return; }
            catch (IOException) when (attempt < 24) { Thread.Sleep(250); }
        }
    }

    static bool WaitForStartup(Process process, string root, string version)
    {
        string status = Path.Combine(root, "status.json");
        var elapsed = Stopwatch.StartNew();
        while (!process.HasExited && elapsed.Elapsed < TimeSpan.FromSeconds(20)) {
            try {
                using var state = JsonDocument.Parse(File.ReadAllText(status));
                if (state.RootElement.GetProperty("Pid").GetInt32() == process.Id &&
                    state.RootElement.GetProperty("Version").GetString() == version)
                    return !process.WaitForExit(750);
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException) { }
            Thread.Sleep(250);
        }
        return false;
    }

    static Process? Start(string path, params string[] args)
    {
        var info = new ProcessStartInfo(path) {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(path)!
        };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info);
    }
}
