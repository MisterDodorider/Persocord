using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Persocord.Models;

namespace Persocord.Services;

public record LinkDuration(string Key, string Label, TimeSpan? Length);

public record ServiceInfo(
    LinkService Id, string Name, long MaxBytes, bool CanKill,
    string[] Durations, string Blurb);

/// <summary>
/// The hosts Persocord can upload to, what each allows, and which one a given
/// choice lands on. Everything the upload menu greys out or highlights comes
/// from here, so the menu and the uploader can never disagree.
/// </summary>
public static class LinkCatalog
{
    public static readonly LinkDuration[] Durations =
    {
        // What the hosts still offered give: Uguu 3 hours, Catbox for good.
        // (Litterbox and Oshi, with the in-between lengths, were removed in
        // 1.5.60: one kept failing with server errors, the other was blocked
        // or failing its security check on many networks.)
        new("3h",  "3 hours",  TimeSpan.FromHours(3)),
        new("forever", "Forever", null),
    };

    /// <summary>
    /// Discord plays a linked video inline only when its servers manage to
    /// cache it, which fails unpredictably above roughly this size. A failure
    /// is remembered for good, so the link never plays even if pasted again.
    /// </summary>
    public const long DiscordInlineLimit = 80_000_000;

    /// <summary>What big clips are shrunk to, leaving room under the limit.</summary>
    public const long ShrinkTarget = 75_000_000;

    // Limits are set a touch under each host's stated maximum, so a file
    // right at the edge is refused here rather than failing mid-upload.
    public static readonly ServiceInfo[] Services =
    {
        new(LinkService.Uguu, "Uguu", 130_000_000, false,
            new[] { "3h" },
            "Fastest in testing, plays inline in Discord. Run by a Swedish company under EU law; keeps only the file name, a fingerprint, your IP and the upload time, deleted when the file expires. Downloads aren't logged. Three hours only."),
        new(LinkService.Catbox, "Catbox", 195_000_000, false,
            new[] { "forever" },
            "VERY SLOW. Uploads take a long time, and Discord often gives up waiting, so the clip may show as a plain link instead of playing. Since July 2026, files nobody opens for 2 years are removed. Can't be taken down from Persocord. Refuses GIFs over 20 MB."),
    };

    // When several hosts could take a clip: the most private first, then the
    // most proven. Kill-link requests only match hosts that support it.
    private static readonly LinkService[] Preference =
        { LinkService.Uguu, LinkService.Catbox };

    /// <summary>
    /// Hosts that failed this session, with why. They drop to the back of
    /// the queue so the next upload tries a working host first.
    /// </summary>
    public static readonly Dictionary<LinkService, string> Failures = new();

    /// <summary>Every host that could take this upload, in the order to try them.</summary>
    public static List<ServiceInfo> Candidates(LinkChoice c, long? size, string? durationKey = null)
    {
        durationKey ??= c.Duration;
        return Preference
            .Select(Info)
            .Where(s => Blocker(s, c, size, durationKey) == null)
            .OrderBy(s => Failures.ContainsKey(s.Id) ? 1 : 0)   // stable: keeps preference within each group
            .ToList();
    }

    /// <summary>
    /// The size that will actually be uploaded: big clips set to shrink go up
    /// at the shrink target, not their full size.
    /// </summary>
    public static long? EffectiveSize(LinkChoice c, long? size, bool isGif = false)
    {
        if (!size.HasValue || isGif) return size;
        return size.Value > DiscordInlineLimit && c.BigClip == BigClipMode.Shrink ? ShrinkTarget : size;
    }

    public static ServiceInfo Info(LinkService id) => Services.First(s => s.Id == id);
    public static LinkDuration? Duration(string key) => Durations.FirstOrDefault(d => d.Key == key);

    /// <summary>Why a host cannot take this upload, or null if it can.</summary>
    public static string? Blocker(ServiceInfo s, LinkChoice c, long? size, string? durationKey)
    {
        if (!c.Services.Contains(s.Id)) return "not selected";
        if (c.NeedKill && !s.CanKill) return "links can't be killed";
        if (size.HasValue && size.Value > s.MaxBytes) return "file too big";
        if (durationKey != null && !s.Durations.Contains(durationKey)) return "no such duration";
        return null;
    }

    /// <summary>The host this choice would upload to, or null if none can.</summary>
    public static ServiceInfo? Pick(LinkChoice c, long? size, string? durationKey = null) =>
        Candidates(c, size, durationKey).FirstOrDefault();

    /// <summary>The largest file this duration could take under the current choices.</summary>
    public static long MaxFor(LinkChoice c, string durationKey)
    {
        long best = 0;
        foreach (var s in Services)
            if (Blocker(s, c, null, durationKey) == null) best = Math.Max(best, s.MaxBytes);
        return best;
    }

    public static string SizeText(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:0.#} GB",
        _ => $"{bytes / 1_000_000.0:0} MB",
    };

    public static string DurationsText(ServiceInfo s) =>
        string.Join(", ", s.Durations.Select(k => Duration(k)?.Label ?? k));
}

/// <summary>
/// Uploads clips and takes links down. Each host takes a plain form post and
/// answers with the link as text.
/// </summary>
public static class LinkUploader
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Persocord/1.6 (Windows clip recorder)");
        return c;
    }

    public static async Task<ClipLink> UploadAsync(
        string path, ServiceInfo service, LinkDuration duration,
        IProgress<double> progress, CancellationToken ct)
    {
        // The upload is named generically. The clip's own title can say more
        // than you might want a stranger to see in a link.
        string uploadName = "clip" + Path.GetExtension(path).ToLowerInvariant();

        // A connection that stops moving is given up on, so the clip can be
        // uploaded again (with no limit at all, a stalled upload stayed "on its
        // way" until Persocord closed). Big uploads to slow hosts take as long
        // as they take: it's only stillness that counts.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallLimit);

        using var form = new MultipartFormDataContent();
        var file = new ProgressFileContent(path, progress, () => { try { stall.CancelAfter(StallLimit); } catch (ObjectDisposedException) { } });
        file.Headers.ContentType = new MediaTypeHeaderValue(
            uploadName.EndsWith(".gif") ? "image/gif" :
            uploadName.EndsWith(".jpg") || uploadName.EndsWith(".jpeg") ? "image/jpeg" :
            uploadName.EndsWith(".png") ? "image/png" : "video/mp4");

        string endpoint;
        switch (service.Id)
        {
            case LinkService.Litterbox:
                endpoint = "https://litterbox.catbox.moe/resources/internals/api.php";
                form.Add(new StringContent("fileupload"), "reqtype");
                form.Add(new StringContent(duration.Key == "3d" ? "72h" : duration.Key), "time");
                form.Add(file, "fileToUpload", uploadName);
                break;

            case LinkService.Catbox:
                endpoint = "https://catbox.moe/user/api.php";
                form.Add(new StringContent("fileupload"), "reqtype");
                form.Add(file, "fileToUpload", uploadName);
                break;

            case LinkService.Uguu:
                endpoint = "https://uguu.se/upload?output=text";
                form.Add(file, "files[]", uploadName);
                break;

            case LinkService.Oshi:
                endpoint = "https://oshi.at";
                form.Add(file, "f", uploadName);
                form.Add(new StringContent(((int)duration.Length!.Value.TotalMinutes).ToString()), "expire");
                break;

            default:
                throw new InvalidOperationException("Unknown host.");
        }

        HttpResponseMessage response;
        string body;
        try
        {
            response = await Http.PostAsync(endpoint, form, stall.Token);
            // Sent: the host gets a while to answer (it may still be storing the file).
            stall.CancelAfter(TimeSpan.FromMinutes(5));
            body = (await response.Content.ReadAsStringAsync(stall.Token)).Trim();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{service.Name} timed out (the upload stopped moving)");
        }
        using var _ = response;

        string? url = null, token = null;
        if (response.IsSuccessStatusCode)
        {
            if (service.Id == LinkService.Oshi) (url, token) = ParseOshi(body);
            else if (body.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = body.Split('\n')[0].Trim();
        }

        if (url == null)
        {
            var detail = body.Length > 200 ? body[..200] : body;
            Log.Write($"{service.Name} upload response ({(int)response.StatusCode}): {body}");
            throw new InvalidOperationException(
                $"{service.Name} refused the upload ({(int)response.StatusCode} {response.StatusCode}). {detail}".Trim());
        }

        return new ClipLink
        {
            Url = url,
            Service = service.Name,
            DurationKey = duration.Key,
            Uploaded = DateTime.Now,
            Expires = duration.Length.HasValue ? DateTime.Now + duration.Length.Value : null,
            DeleteToken = token,
        };
    }

    /// <summary>
    /// Oshi answers with a few labelled lines: the download address, a
    /// management address for destroying the upload, and a Tor address. The
    /// exact wording isn't documented, so this reads it loosely and logs it.
    /// </summary>
    private static (string? url, string? admin) ParseOshi(string body)
    {
        Log.Write("Oshi response: " + body.Replace("\n", " | "));
        string? url = null, admin = null;

        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            int at = line.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var link = line[at..].Split(' ', '\t')[0].Trim();
            var label = line[..at].ToUpperInvariant();

            if (label.Contains("MANAGE") || label.Contains("ADMIN") || link.Contains("/a/")) admin ??= link;
            else if (!label.Contains("TOR") && !link.Contains(".onion")) url ??= link;
        }
        return (url, admin);
    }

    /// <summary>
    /// A link to x266.mov's embed page for a video. The page hands Discord a
    /// ready-made preview image, so Discord plays big files it would
    /// otherwise show as a plain link.
    /// </summary>
    public static string X266Link(string videoUrl, string? thumbUrl, int width, int height)
    {
        var q = new List<string>();
        if (!string.IsNullOrEmpty(thumbUrl)) q.Add("i=" + thumbUrl);
        if (width > 0 && height > 0) { q.Add("w=" + width); q.Add("h=" + height); }
        return "https://x266.mov/e/" + videoUrl + (q.Count > 0 ? "?" + string.Join("&", q) : "");
    }

    /// <summary>Streams the file and reports how far along it is.</summary>
    /// <summary>How long an upload may go without sending anything before it's given up on.</summary>
    private static readonly TimeSpan StallLimit = TimeSpan.FromMinutes(3);

    private sealed class ProgressFileContent : HttpContent
    {
        private readonly string _path;
        private readonly IProgress<double> _progress;
        private readonly Action _moved;

        public ProgressFileContent(string path, IProgress<double> progress, Action moved)
        {
            _path = path;
            _progress = progress;
            _moved = moved;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, true);
            var buffer = new byte[1 << 16];
            long total = file.Length, sent = 0;
            int read;
            while ((read = await file.ReadAsync(buffer)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read));
                _moved();
                sent += read;
                _progress.Report(total > 0 ? sent / (double)total : 1);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = new FileInfo(_path).Length;
            return true;
        }
    }
}
