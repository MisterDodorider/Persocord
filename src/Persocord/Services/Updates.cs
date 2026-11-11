using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Persocord.Services;

/// <summary>
/// New versions, from Persocord's GitHub releases. Every start asks GitHub
/// once for the newest release (nothing about the PC is sent; GitHub needs
/// no account for this). When there's a newer one, the clip list shows a
/// banner, never anything over a game. Its Update button either downloads
/// the release and runs its installer (which closes Persocord, builds the
/// new version and opens it again), or opens the release page.
/// </summary>
public static class Updates
{
    public const string Repo = "MisterDodorider/Persocord";
    public static string RepoPage => $"https://github.com/{Repo}";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases/latest";

    /// <param name="Label">The version as written on the release ("1.6.50").</param>
    public sealed record Release(Version Version, string Label, string Page, string? ZipUrl, long ZipSize);

    /// <summary>This copy was just installed by an update (the installer opened it with --updated).</summary>
    public static bool JustUpdated { get; set; }

    /// <summary>The installer reopened this copy because the update didn't finish (--update-failed).</summary>
    public static bool LastTryFailed { get; set; }

    /// <summary>This version as people see it.</summary>
    public static string CurrentLabel { get; } = Label(Paths.AppVersion) ?? Paths.AppVersion;

    /// <summary>The newer release found at start, or null.</summary>
    public static Release? Available { get; private set; }

    /// <summary>When the last check got an answer, and what it said (for the diagnostics report).</summary>
    public static string Status { get; private set; } = "not checked yet";

    /// <summary>Something changed: a release was found, or the download moved along.</summary>
    public static event Action? Changed;

    /// <summary>The download's progress (0 to 1), or null when nothing is downloading.</summary>
    public static double? Progress { get; private set; }

    /// <summary>The last update attempt's problem, shown on the banner.</summary>
    public static string? Problem { get; private set; }

    public static Version Current { get; } = ParseVersion(Paths.AppVersion) ?? new Version(0, 0);

    private static Folder? _downloading;
    private static int _checking;
    private static CancellationTokenSource? _cancel;

    /// <summary>Where downloads and their unpacked files go (removed again at the next start).</summary>
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Persocord", "Updates");

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        // GitHub turns away requests that don't say what they are.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Persocord/{Current.ToString()}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /* ---- the check -------------------------------------------------------- */

    /// <summary>
    /// Asks GitHub for the newest release. Persocord often starts with
    /// Windows, before the internet is up, so a failed check tries again a
    /// few times over the next quarter of an hour.
    /// </summary>
    public static async Task CheckAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        TimeSpan[] waits = { TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(10) };
        foreach (var wait in waits)
        {
            if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
            try
            {
                var found = await FetchLatestAsync().ConfigureAwait(false);
                Status = found == null
                    ? $"checked {DateTime.Now:HH:mm}, no release found"
                    : $"checked {DateTime.Now:HH:mm}, newest is {found.Label}";
                if (found != null && found.Version > Current)
                {
                    Available = found;
                    Log.Write($"Update: Persocord {found.Label} is out (this is {CurrentLabel})");
                    Changed?.Invoke();
                }
                else Log.Write($"Update check: up to date ({CurrentLabel})");
                return;
            }
            catch (Exception ex)
            {
                Status = $"couldn't check ({ex.GetType().Name}: {ex.Message})";
                Log.Write("Update check failed: " + ex.Message);
            }
        }
    }

    private static async Task<Release?> FetchLatestAsync()
    {
        using var http = NewClient(TimeSpan.FromSeconds(20));
        using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest").ConfigureAwait(false);
        // No release published yet.
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
        var root = doc.RootElement;

        string tag = Str(root, "tag_name") ?? "";
        var version = ParseVersion(tag);
        if (version == null) return null;
        string page = Str(root, "html_url") ?? ReleasesPage;

        // The zip attached to the release; GitHub's own source zip works too
        // (it holds the same files), so it's the fallback.
        string? zip = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            var zips = assets.EnumerateArray()
                .Where(a => (Str(a, "name") ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => (Str(a, "name") ?? "").Contains("Persocord", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (zips.Count > 0)
            {
                zip = Str(zips[0], "browser_download_url");
                if (zips[0].TryGetProperty("size", out var s) && s.TryGetInt64(out var sz)) size = sz;
            }
        }
        zip ??= Str(root, "zipball_url");
        if (zip != null && !FromGitHub(zip)) zip = null;
        return new Release(version, Label(tag) ?? version.ToString(), page, zip, size);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Downloads only ever come from GitHub itself.</summary>
    private static bool FromGitHub(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         u.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>"v1.6.40", "1.6.40", "Persocord 1.6.40" all read as 1.6.40.</summary>
    public static Version? ParseVersion(string? text) =>
        Label(text) is { } l && Version.TryParse(l, out var v) ? v : null;

    /// <summary>Just the version number in a text, as written.</summary>
    public static string? Label(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"\d+(\.\d+){1,3}");
        return m.Success ? m.Value : null;
    }

    /// <summary>A note on the banner (null takes it away).</summary>
    public static void Say(string? problem)
    {
        Problem = problem;
        Changed?.Invoke();
    }

    /// <summary>Opens a page in the browser.</summary>
    public static void OpenPage(string url)
    {
        if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) url = ReleasesPage;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Error("open page", ex); }
    }

    /* ---- installing --------------------------------------------------------- */

    public static bool Downloading => _downloading != null;

    /// <summary>
    /// Downloads the release, unpacks it and returns the installer to run, or
    /// null when it was cancelled or failed (Problem then says why).
    /// </summary>
    public static async Task<string?> DownloadAsync()
    {
        var release = Available;
        if (release?.ZipUrl == null || _downloading != null) return null;
        Problem = null;
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        var folder = _downloading = new Folder(Path.Combine(Dir, release.Version.ToString()));
        Progress = 0;
        Changed?.Invoke();
        try
        {
            Directory.CreateDirectory(Dir);
            if (Directory.Exists(folder.Path)) Directory.Delete(folder.Path, true);
            string zip = folder.Path + ".zip";

            using (var http = NewClient(TimeSpan.FromMinutes(10)))
            using (var resp = await http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? release.ZipSize;
                await using var src = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var dst = File.Create(zip);
                var buf = new byte[81920];
                long got = 0;
                var shown = Stopwatch.StartNew();
                int n;
                while ((n = await src.ReadAsync(buf, token).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), token).ConfigureAwait(false);
                    got += n;
                    if (total > 0 && shown.ElapsedMilliseconds > 150)
                    {
                        shown.Restart();
                        Progress = Math.Min(1, (double)got / total);
                        Changed?.Invoke();
                    }
                }
            }
            Progress = 1;
            Changed?.Invoke();

            // .NET refuses entries that would land outside the folder.
            ZipFile.ExtractToDirectory(zip, folder.Path, overwriteFiles: true);
            File.Delete(zip);

            // The installer sits in tools\ beside the source, one folder down
            // in the zip (Persocord\, or GitHub's own name for its source zip).
            var installer = Directory.EnumerateFiles(folder.Path, "install.ps1", SearchOption.AllDirectories)
                .FirstOrDefault(p =>
                {
                    var tools = Path.GetDirectoryName(p)!;
                    var root = Path.GetDirectoryName(tools)!;
                    return Path.GetFileName(tools).Equals("tools", StringComparison.OrdinalIgnoreCase) &&
                           File.Exists(Path.Combine(root, "src", "Persocord", "Persocord.csproj"));
                });
            if (installer == null) throw new InvalidDataException("the download has no installer in it");
            Log.Write($"Update: {release.Label} downloaded");
            return installer;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Log.Write("Update: download cancelled");
            return null;
        }
        catch (Exception ex)
        {
            Log.Write("Update: download failed: " + ex);
            Problem = "The download didn't work. Check your internet and try again.";
            return null;
        }
        finally
        {
            _downloading = null;
            Progress = null;
            Changed?.Invoke();
        }
    }

    public static void CancelDownload() => _cancel?.Cancel();

    /// <summary>
    /// Runs the installer in its own window. It closes this copy (Persocord
    /// closes itself right after), builds the new version, and opens it
    /// with the clip list. If anything goes wrong it reopens this version.
    /// </summary>
    public static bool RunInstaller(string installer)
    {
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{installer}\" -Auto",
                WorkingDirectory = Path.GetDirectoryName(Path.GetDirectoryName(installer)!)!,
                UseShellExecute = true,
            })?.Dispose();
            Log.Write("Update: installer started; closing for it");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("update installer", ex);
            Problem = "The installer wouldn't start. Use Open page to get it by hand.";
            Changed?.Invoke();
            return false;
        }
    }

    /// <summary>Clears what an earlier update left behind (called a while after starting).</summary>
    public static void CleanUp()
    {
        if (_downloading != null || !Directory.Exists(Dir)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(Dir))
        {
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, true);
                else File.Delete(entry);
            }
            catch { /* still in use (an installer window left open); next start */ }
        }
    }

    private sealed record Folder(string Path);
}
