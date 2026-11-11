using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Persocord.Models;

namespace Persocord.Services;

/// <summary>How the library folder stands right now.</summary>
public enum FolderStatus { Ok, Missing, Relocated }

public record DiskReport(
    long Used,          // bytes the library occupies
    long Cap,           // hard ceiling, from the drive size
    long WarnAt,        // where the gauge turns red
    long DriveTotal,
    long DriveFree,
    bool OverWarn,
    bool OverCap)
{
    public double FractionOfCap => Cap <= 0 ? 0 : Math.Min(1.5, Used / (double)Cap);
    public double WarnFraction  => Cap <= 0 ? 0.8 : WarnAt / (double)Cap;
}

/// <summary>
/// Owns the clips folder: what is in it, how much room it is allowed, and
/// where it went if somebody moved it.
/// </summary>
public class LibraryService : IDisposable
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private const string MarkerFile = ".persocord";

    private readonly SettingsService _settings;
    private FileSystemWatcher? _parentWatcher;
    private FileSystemWatcher? _clipWatcher;
    private Timer? _clipDebounce;
    private readonly object _watchLock = new();

    /// <summary>
    /// Clip files appeared, vanished or were renamed in the folder while
    /// Persocord was running. Raised off the UI thread, after a short pause
    /// so a file still being copied has time to finish.
    /// </summary>
    public event Action? FilesChanged;
    private Timer? _heartbeat;
    private CancellationTokenSource? _searchCts;

    public ObservableCollection<ClipRecord> Items { get; } = new();

    public event Action? Changed;
    public event Action<FolderStatus, string>? FolderTrouble; // status, message
    public event Action<string>? FolderRecovered;             // new path

    public LibraryService(SettingsService settings) => _settings = settings;

    /* ------------------------------------------------------------------ */
    /* paths                                                               */
    /* ------------------------------------------------------------------ */

    public string Folder
    {
        get
        {
            var f = _settings.Current.LibraryFolder;
            if (!string.IsNullOrWhiteSpace(f)) return f;
            return _defaultFolder ??= DefaultFolder();
        }
    }

    private string? _defaultFolder;

    /// <summary>
    /// Where clips go when no folder has been chosen: Videos\Persocord on this
    /// PC, never OneDrive. OneDrive's folder backup quietly moves "Videos"
    /// into OneDrive, which would upload every clip and fill the cloud
    /// storage, so in that case the local Videos folder in the user profile
    /// is used instead. A library already living in the old default place
    /// stays there, so nobody's clips go missing.
    /// </summary>
    private static string DefaultFolder()
    {
        var known = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var legacy = string.IsNullOrEmpty(known) ? null : Path.Combine(known, "Persocord");
        if (legacy != null && File.Exists(Path.Combine(legacy, "library.json"))) return legacy;

        if (!string.IsNullOrEmpty(known) && !IsOneDrive(known)) return Path.Combine(known, "Persocord");

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localVideos = Path.Combine(profile, "Videos");
        return IsOneDrive(localVideos)
            ? Path.Combine(profile, "Persocord Clips")
            : Path.Combine(localVideos, "Persocord");
    }

    private static bool IsOneDrive(string path)
    {
        foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var root = Environment.GetEnvironmentVariable(v);
            if (!string.IsNullOrEmpty(root) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return path.Contains(Path.DirectorySeparatorChar + "OneDrive", StringComparison.OrdinalIgnoreCase);
    }

    public string PathOf(ClipRecord c) => Path.Combine(Folder, c.FileName);

    /// <summary>
    /// Deletes thumbnails whose clip no longer exists: in Persocord's own
    /// thumbnails folder, and the hidden ones older versions kept beside the
    /// clips (left behind when clips were deleted in Explorer).
    /// </summary>
    public void CleanThumbnails()
    {
        try
        {
            List<ClipRecord> items;
            lock (Items) items = Items.ToList();
            var ids = new HashSet<string>(items.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
            var wanted = new HashSet<string>(items.Where(c => !string.IsNullOrEmpty(c.ThumbName))
                .Select(c => Path.GetFullPath(Path.Combine(Folder, c.ThumbName!))), StringComparer.OrdinalIgnoreCase);
            int removed = 0;
            foreach (var f in Directory.EnumerateFiles(Paths.Thumbs, "*.jpg"))
                if (!wanted.Contains(Path.GetFullPath(f))) { try { File.Delete(f); removed++; } catch { } }
            if (Directory.Exists(Folder))
                foreach (var f in Directory.EnumerateFiles(Folder, "*.thumb.jpg", SearchOption.AllDirectories))
                {
                    var id = Path.GetFileName(f)[..^".thumb.jpg".Length];
                    if (!ids.Contains(id) && !wanted.Contains(Path.GetFullPath(f))) { try { File.Delete(f); removed++; } catch { } }
                }
            if (removed > 0) Log.Write($"Removed {removed} thumbnails of clips that are gone");
        }
        catch (Exception ex) { Log.Error("clean thumbnails", ex); }
    }

    public string? ThumbPathOf(ClipRecord c) =>
        string.IsNullOrEmpty(c.ThumbName) ? null : Path.Combine(Folder, c.ThumbName);

    private string IndexFile => Path.Combine(Folder, "library.json");

    public bool FolderExists => Directory.Exists(Folder);

    /* ------------------------------------------------------------------ */
    /* lifecycle                                                           */
    /* ------------------------------------------------------------------ */

    public void Start()
    {
        EnsureFolder();
        Load();
        WatchFolder();

        // The watcher misses whole classes of change — a drive going offline,
        // a move performed while the app was closed, a folder deleted from
        // another machine over a network share. A slow poll catches those.
        _heartbeat = new Timer(_ => CheckFolder(), null, 5000, 15000);
        _ = Task.Run(Recount);
    }

    public void EnsureFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);

            var s = _settings.Current;
            if (string.IsNullOrEmpty(s.LibraryId))
            {
                s.LibraryId = Guid.NewGuid().ToString("N");
                _settings.Save();
            }

            // A marker carrying the library's id is what makes a moved folder
            // findable later. Without it a move is indistinguishable from a
            // delete followed by an unrelated folder appearing elsewhere.
            var marker = Path.Combine(Folder, MarkerFile);
            if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != s.LibraryId)
            {
                File.WriteAllText(marker, s.LibraryId);
                File.SetAttributes(marker, FileAttributes.Hidden);
            }
        }
        catch (Exception ex) { Log.Error("ensure folder", ex); }
    }

    public void Dispose()
    {
        _heartbeat?.Dispose();
        _parentWatcher?.Dispose();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
    }

    /* ------------------------------------------------------------------ */
    /* index                                                               */
    /* ------------------------------------------------------------------ */

    public void Load()
    {
        // The list is the window's: changed only on its thread. (Reloading from
        // the folder check's own thread, after the folder came back, could
        // change it while the window was reading it.)
        var ui = System.Windows.Application.Current?.Dispatcher;
        if (ui != null && !ui.CheckAccess()) { ui.BeginInvoke(new Action(Load)); return; }
        // Once the list is settled: thumbnails whose clip is gone are removed.
        _ = Task.Run(async () => { await Task.Delay(5000); CleanThumbnails(); });
        try
        {
            Items.Clear();
            if (!File.Exists(IndexFile)) { ReconcileWithDisk(); return; }

            var list = JsonSerializer.Deserialize<List<ClipRecord>>(
                File.ReadAllText(IndexFile), Opts) ?? new();

            foreach (var c in list.OrderByDescending(c => c.Created))
                Items.Add(c);

            ReconcileWithDisk();
        }
        catch (Exception ex)
        {
            // The index holds titles and Keep marks, which cannot be rebuilt
            // from the files. Set it aside before anything saves over it.
            Log.Error("library load", ex);
            try
            {
                File.Copy(IndexFile, $"{IndexFile}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}", true);
            }
            catch { }
            ReconcileWithDisk();
        }
    }

    public void Save()
    {
        // Read on the list's own thread: from elsewhere it could be mid-change,
        // and the save was then skipped (logged, but the change wasn't kept).
        var ui = System.Windows.Application.Current?.Dispatcher;
        if (ui != null && !ui.CheckAccess() && !ui.HasShutdownStarted) { ui.BeginInvoke(new Action(Save)); return; }
        try
        {
            if (!FolderExists) return;

            // The index lives inside the library, so moving the folder carries
            // the clip titles and favourites along with the files.
            SettingsService.WriteWhole(IndexFile, JsonSerializer.Serialize(Items.ToList(), Opts));
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Error("library save", ex); }
    }

    /// <summary>
    /// Brings the index in line with what is actually on disk: files deleted
    /// behind the app's back are dropped, and videos dropped into the folder
    /// by hand are picked up.
    /// </summary>
    public void ReconcileWithDisk()
    {
        var ui = System.Windows.Application.Current?.Dispatcher;
        if (ui != null && !ui.CheckAccess()) { ui.BeginInvoke(new Action(ReconcileWithDisk)); return; }
        try
        {
            if (!FolderExists) return;

            // Every clip file in the folder and its subfolders, by its path
            // relative to the clips folder ("Roblox\\clip.mp4").
            var onDisk = AllClipFiles()
                .ToDictionary(p => Path.GetRelativePath(Folder, p), StringComparer.OrdinalIgnoreCase);

            // The full-quality original behind a shrunk clip belongs to that
            // clip, not to a new one.
            foreach (var c in Items)
                if (!string.IsNullOrEmpty(c.OriginalFileName)) onDisk.Remove(c.OriginalFileName);

            // Two records for one file (a race between saving and the folder
            // watcher): keep the real one, not the "found" stand-in.
            foreach (var group in Items.GroupBy(c => c.FileName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList())
            {
                var keep = group.FirstOrDefault(c => !c.IsImported) ?? group.First();
                foreach (var extra in group) if (extra != keep) Items.Remove(extra);
            }

            var missing = new List<ClipRecord>();
            foreach (var c in Items)
            {
                if (onDisk.ContainsKey(c.FileName))
                {
                    var fi = new FileInfo(Path.Combine(Folder, c.FileName));
                    c.Size = fi.Length;
                    c.State = ClipState.Ready;
                    onDisk.Remove(c.FileName);
                }
                else missing.Add(c);
            }

            // A clip moved between folders in Explorer: same file name and
            // size somewhere else. Follow it, keeping its tags, link and Keep.
            foreach (var c in missing)
            {
                var name = Path.GetFileName(c.FileName);
                var moved = onDisk.Keys.FirstOrDefault(k =>
                    string.Equals(Path.GetFileName(k), name, StringComparison.OrdinalIgnoreCase) &&
                    new FileInfo(Path.Combine(Folder, k)).Length == c.Size);
                if (moved != null)
                {
                    c.FileName = moved;
                    c.State = ClipState.Ready;
                    onDisk.Remove(moved);
                }
                else Items.Remove(c);
            }

            foreach (var name in onDisk.Keys)
            {
                if (Busy(name)) continue;   // being saved, moved or deleted by Persocord
                var fi = new FileInfo(Path.Combine(Folder, name));
                // Copying a file stamps it with the copy's time; the last
                // write time is when the recording actually happened.
                var when = fi.LastWriteTime < fi.CreationTime ? fi.LastWriteTime : fi.CreationTime;
                Items.Insert(0, new ClipRecord
                {
                    Title = Path.GetFileNameWithoutExtension(name),
                    FileName = name,
                    Size = fi.Length,
                    Created = when,
                    State = ClipState.Ready,
                    Note = "Imported",
                });
            }

            var ordered = Items.OrderByDescending(c => c.Created).ToList();
            Items.Clear();
            foreach (var c in ordered) Items.Add(c);

            Changed?.Invoke();
            _ = Task.Run(Recount);
        }
        catch (Exception ex) { Log.Error("reconcile", ex); }
    }

    /// <summary>
    /// Watches the clips folder itself, so files copied, duplicated or
    /// renamed in Explorer show up without restarting Persocord.
    /// </summary>
    private void WatchClips()
    {
        try
        {
            _clipWatcher?.Dispose();
            _clipWatcher = null;
            if (!FolderExists) return;

            _clipWatcher = new FileSystemWatcher(Folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.DirectoryName,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };

            void Poke(string path)
            {
                // Runs on the watcher's own thread: an escaped error would end Persocord.
                try
                {
                    // Clip files, and folders being made, renamed or removed.
                    if (!IsClipFile(path) && Path.HasExtension(path)) return;
                    // Hidden folders inside the library (and Persocord's own) are
                    // ignored; the library folder's own name doesn't matter.
                    var inside = Path.GetRelativePath(Folder, path);
                    if (inside.Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith('.'))) return;
                    // One quiet moment after a burst of changes, then a single look.
                    lock (_watchLock)
                    {
                        if (_clipDebounce == null)
                            _clipDebounce = new Timer(_ => { try { FilesChanged?.Invoke(); } catch (Exception ex) { Log.Error("clips changed", ex); } }, null, 1500, Timeout.Infinite);
                        else _clipDebounce.Change(1500, Timeout.Infinite);
                    }
                }
                catch (Exception ex) { Log.Error("watch clips", ex); }
            }

            _clipWatcher.Created += (_, e) => Poke(e.FullPath);
            _clipWatcher.Deleted += (_, e) => Poke(e.FullPath);
            _clipWatcher.Changed += (_, e) => Poke(e.FullPath);
            _clipWatcher.Renamed += (_, e) => { Poke(e.FullPath); Poke(e.OldFullPath); };
        }
        catch (Exception ex) { Log.Error("watch clips", ex); }
    }

    /// <summary>
    /// Clip files anywhere under the clips folder. Folders whose names start
    /// with a dot (Persocord's own, or hidden ones) are left alone.
    /// </summary>
    private IEnumerable<string> AllClipFiles()
    {
        var stack = new Stack<string>();
        stack.Push(Folder);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> files, dirs;
            try { files = Directory.EnumerateFiles(dir).ToList(); dirs = Directory.EnumerateDirectories(dir).ToList(); }
            catch (Exception ex) { Log.Error("list " + dir, ex); continue; }

            foreach (var f in files) if (IsClipFile(f)) yield return f;
            foreach (var d in dirs)
                if (!Path.GetFileName(d).StartsWith('.') &&
                    !new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.Hidden))
                    stack.Push(d);
        }
    }

    /* ---- folders --------------------------------------------------------- */

    /// <summary>The folder a clip sits in, relative to the clips folder ("" for the top).</summary>
    public static string FolderOf(ClipRecord c) => Path.GetDirectoryName(c.FileName) ?? "";

    /// <summary>The folders directly inside one folder (relative paths), alphabetically.</summary>
    public List<string> SubfoldersOf(string relative)
    {
        try
        {
            var dir = Path.Combine(Folder, relative);
            if (!Directory.Exists(dir)) return new();
            return Directory.EnumerateDirectories(dir)
                .Where(d => !Path.GetFileName(d).StartsWith('.') &&
                            !new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.Hidden))
                .Select(d => Path.GetRelativePath(Folder, d))
                .OrderBy(d => d, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex) { Log.Error("list folders", ex); return new(); }
    }

    /// <summary>Every folder in the library, for "Move to…".</summary>
    public List<string> AllFolders()
    {
        var all = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue("");
        while (queue.Count > 0)
            foreach (var sub in SubfoldersOf(queue.Dequeue())) { all.Add(sub); queue.Enqueue(sub); }
        return all;
    }

    private static readonly char[] BadName = Path.GetInvalidFileNameChars();

    public string? CreateFolder(string parent, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.IndexOfAny(BadName) >= 0 || name.StartsWith('.')) return "That name can't be used for a folder.";
        var path = Path.Combine(Folder, parent, name);
        if (Directory.Exists(path)) return "A folder with that name is already here.";
        try { Directory.CreateDirectory(path); return null; }
        catch (Exception ex) { Log.Error("create folder", ex); return "The folder couldn't be created."; }
    }

    public string? RenameFolder(string relative, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName.IndexOfAny(BadName) >= 0 || newName.StartsWith('.')) return "That name can't be used for a folder.";
        var parent = Path.GetDirectoryName(relative) ?? "";
        var target = Path.Combine(parent, newName);
        if (string.Equals(target, relative, StringComparison.Ordinal)) return null;
        try
        {
            var from = Path.Combine(Folder, relative);
            var to = Path.Combine(Folder, target);
            if (Directory.Exists(to) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                return "A folder with that name is already here.";
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                // Only the capitals change: Windows needs a detour.
                var tmp = from + ".rename";
                Directory.Move(from, tmp);
                Directory.Move(tmp, to);
            }
            else Directory.Move(from, to);

            var prefix = relative + Path.DirectorySeparatorChar;
            foreach (var c in Items)
            {
                if (c.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    c.FileName = Path.Combine(target, c.FileName[prefix.Length..]);
                if (c.OriginalFileName?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
                    c.OriginalFileName = Path.Combine(target, c.OriginalFileName[prefix.Length..]);
            }
            Save();
            return null;
        }
        catch (Exception ex) { Log.Error("rename folder", ex); return "The folder couldn't be renamed. Is something using a file in it?"; }
    }

    /// <summary>Deletes a folder, but only an empty one: clips are never deleted this way.</summary>
    public string? DeleteFolder(string relative)
    {
        try
        {
            var path = Path.Combine(Folder, relative);
            if (Directory.EnumerateFileSystemEntries(path).Any())
                return "Only empty folders can be deleted. Move or delete the clips inside first.";
            Directory.Delete(path);
            return null;
        }
        catch (Exception ex) { Log.Error("delete folder", ex); return "The folder couldn't be deleted."; }
    }

    /// <summary>Moves a clip (and the original behind a shrunk clip) into another folder.</summary>
    public bool MoveClip(ClipRecord clip, string targetFolder)
    {
        try
        {
            if (string.Equals(FolderOf(clip), targetFolder, StringComparison.OrdinalIgnoreCase)) return true;
            Directory.CreateDirectory(Path.Combine(Folder, targetFolder));

            clip.FileName = MoveInto(clip.FileName, targetFolder);
            if (!string.IsNullOrEmpty(clip.OriginalFileName) && File.Exists(Path.Combine(Folder, clip.OriginalFileName)))
                clip.OriginalFileName = MoveInto(clip.OriginalFileName, targetFolder);
            Save();
            return true;
        }
        catch (Exception ex) { Log.Error("move clip", ex); return false; }
    }

    /// <summary>
    /// Renames a clip and its file (in the same folder, same extension).
    /// Returns false when only the name changed: the file was in use, so it
    /// kept its old name.
    /// </summary>
    public bool RenameClip(ClipRecord clip, string title)
    {
        clip.Title = title;
        try
        {
            var bad = Path.GetInvalidFileNameChars();
            var stem = new string(title.Where(c => !bad.Contains(c)).ToArray()).Trim(' ', '.');
            if (stem.Length > 120) stem = stem[..120].Trim(' ', '.');
            if (stem.Length > 0 && !Path.IsPathRooted(clip.FileName))
            {
                var folder = FolderOf(clip);
                var ext = Path.GetExtension(clip.FileName);
                var dest = Path.Combine(folder, stem + ext);
                if (!string.Equals(dest, clip.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    for (int n = 2; File.Exists(Path.Combine(Folder, dest)); n++)
                        dest = Path.Combine(folder, $"{stem} ({n}){ext}");
                    Filing(dest);
                    File.Move(Path.Combine(Folder, clip.FileName), Path.Combine(Folder, dest));
                    clip.FileName = dest;
                }
            }
            Save();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("rename clip file", ex);
            Save();
            return false;
        }
    }

    private string MoveInto(string relative, string targetFolder)
    {
        var name = Path.GetFileNameWithoutExtension(relative);
        var ext = Path.GetExtension(relative);
        var dest = Path.Combine(targetFolder, name + ext);
        for (int n = 2; File.Exists(Path.Combine(Folder, dest)); n++)
            dest = Path.Combine(targetFolder, $"{name} ({n}){ext}");
        Filing(dest);
        File.Move(Path.Combine(Folder, relative), Path.Combine(Folder, dest));
        return dest;
    }

    /// <summary>
    /// The top-level folder named after an app, if there is one: "Roblox"
    /// catches "Roblox" and "Roblox Studio"; "Deadlock" catches "deadlock".
    /// </summary>
    public string? FolderForApp(string app)
    {
        if (string.IsNullOrWhiteSpace(app)) return null;
        app = app.Trim();
        foreach (var f in SubfoldersOf(""))
        {
            var name = Path.GetFileName(f);
            if (string.Equals(name, app, StringComparison.OrdinalIgnoreCase)) return f;
            if (app.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase) ||
                app.StartsWith(name, StringComparison.OrdinalIgnoreCase) && name.Length >= 4)
                return f;
        }
        return null;
    }

    /// <summary>
    /// The folder for a game or app, made if there isn't one yet. Clips of
    /// the desktop (no app) go in "Desktop". Existing folders with a
    /// matching name are used as they are.
    /// </summary>
    public string? FolderForApp(string app, bool create)
    {
        var found = FolderForApp(app);
        if (found != null || !create) return found;
        var name = string.IsNullOrWhiteSpace(app) ? "Desktop" : CleanFolderName(app);
        if (name.Length == 0) name = "Desktop";
        found = FolderForApp(name);
        if (found != null) return found;
        try
        {
            Directory.CreateDirectory(Path.Combine(Folder, name));
            Log.Write($"Made a folder for {name}");
            return name;
        }
        catch (Exception ex) { Log.Error("make app folder", ex); return null; }
    }

    private static string CleanFolderName(string app)
    {
        var bad = Path.GetInvalidFileNameChars();
        var name = new string(app.Trim().Where(c => !bad.Contains(c)).ToArray()).Trim(' ', '.');
        if (name.Length > 60) name = name[..60].Trim(' ', '.');
        // "deadlock" → "Deadlock"; names with their own capitals stay as they are.
        if (name.Length > 0 && name == name.ToLowerInvariant())
            name = char.ToUpperInvariant(name[0]) + name[1..];
        return name;
    }

    private static bool IsClipFile(string path)
    {
        var e = Path.GetExtension(path).ToLowerInvariant();
        return e is ".mp4" or ".gif" or ".mkv" or ".mov" or ".webm" or ".m4v" or ".avi";
    }

    /* ---- files Persocord is busy with ------------------------------------ */

    // Files Persocord is writing or moving right now, and files waiting to be
    // deleted. The folder watcher leaves these alone, so a clip being saved
    // is never listed twice, and a deleted one never comes back as "found".
    private readonly HashSet<string> _filing = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deleting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Marks a file (relative path) as Persocord's own for a few seconds.</summary>
    public void Filing(string relative, int seconds = 8)
    {
        lock (_filing) _filing.Add(relative);
        _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ => { lock (_filing) _filing.Remove(relative); });
    }

    private bool Busy(string relative)
    {
        lock (_filing) if (_filing.Contains(relative)) return true;
        lock (_deleting) return _deleting.Contains(relative);
    }

    public void Add(ClipRecord clip)
    {
        // The folder watcher may have spotted the file before this call and
        // listed it as found; the real record replaces that stand-in.
        for (int i = Items.Count - 1; i >= 0; i--)
            if (string.Equals(Items[i].FileName, clip.FileName, StringComparison.OrdinalIgnoreCase))
                Items.RemoveAt(i);

        Items.Insert(0, clip);
        Save();
        if (Interlocked.Read(ref _usedBytes) >= 0) Interlocked.Add(ref _usedBytes, clip.Size);
        _ = Task.Run(Recount);
    }

    /// <summary>
    /// Deletes a clip: the video goes to the Recycle Bin (unless switched off),
    /// the slow part (the Recycle Bin can take a moment) off the UI thread.
    /// The list changes back on the caller's thread. Thumbnails are simply
    /// deleted: they are rebuilt on demand.
    /// </summary>
    public async Task RemoveAsync(ClipRecord clip, bool permanent = false)
    {
        Items.Remove(clip);   // gone from view at once
        Save();
        await Task.Run(() => DeleteFilesOf(clip, permanent));
        _ = Task.Run(Recount);
    }

    private void DeleteFilesOf(ClipRecord clip, bool permanent)
    {
        bool toBin = !permanent && _settings.Current.DeleteToRecycleBin;
        DeleteWithRetry(clip.FileName, toBin);
        if (!string.IsNullOrEmpty(clip.OriginalFileName)) DeleteWithRetry(clip.OriginalFileName, toBin);
        try
        {
            var t = ThumbPathOf(clip);
            if (t != null && File.Exists(t)) File.Delete(t);
        }
        catch (Exception ex) { Log.Error("delete thumbnail", ex); }
        // The clip's edit and its worked-out Unknown sound go with it.
        try
        {
            var edits = Path.Combine(Paths.Root, "edits");
            if (Directory.Exists(edits))
                foreach (var f in Directory.EnumerateFiles(edits, clip.Id + "*")) try { File.Delete(f); } catch { }
        }
        catch (Exception ex) { Log.Error("delete edit", ex); }
    }

    /// <summary>
    /// Deletes a clip file even if something (a thumbnail being made, a
    /// preview, antivirus) has it open for a moment: it retries, and if the
    /// file is still busy, keeps trying in the background. Meanwhile it's kept
    /// off the list, so a deleted clip never reappears as "found".
    /// </summary>
    private void DeleteWithRetry(string relative, bool toBin)
    {
        var path = Path.Combine(Folder, relative);
        lock (_deleting) _deleting.Add(relative);

        for (int i = 0; i < 10; i++)
        {
            try { DeleteClipFile(path, toBin); } catch (Exception ex) when (i == 9 && ex is not IOException) { Log.Error("delete clip", ex); } catch { }
            if (!File.Exists(path)) { lock (_deleting) _deleting.Remove(relative); return; }
            Thread.Sleep(300);
        }

        Log.Write($"{relative} is in use; it will be deleted as soon as it's free");
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 120 && File.Exists(path); i++)
            {
                await Task.Delay(5000);
                try { DeleteClipFile(path, toBin); } catch { }
            }
            if (File.Exists(path)) Log.Write($"Gave up deleting {relative}");
            lock (_deleting) _deleting.Remove(relative);
        });
    }

    /// <summary>
    /// Swaps a clip's file for an edited one. The old file goes to the Recycle
    /// Bin when that's switched on, so "replace" can still be undone.
    /// </summary>
    public bool ReplaceFile(ClipRecord clip, string newFile, string? newExtension = null)
    {
        try
        {
            var old = PathOf(clip);
            var dest = old;
            // The clip stays in its own folder (a game folder like "Roblox\").
            // Keeping only the file name used to drop that folder, so the clip
            // pointed at a file that didn't exist and the edit sat untracked.
            var folder = Path.GetDirectoryName(clip.FileName) ?? "";
            if (newExtension != null)
            {
                // A new kind of file gets its matching extension, under a
                // name nothing else in the folder is using.
                var stem = Path.Combine(Path.GetDirectoryName(old) ?? Folder, Path.GetFileNameWithoutExtension(old));
                dest = stem + newExtension;
                for (int n = 2; File.Exists(dest) && !string.Equals(dest, old, StringComparison.OrdinalIgnoreCase); n++)
                    dest = $"{stem} ({n}){newExtension}";
            }

            // The new file goes next to the original first. Across drives this is
            // a copy, done while the original is still safely in place; the
            // swap below is then just a rename.
            var staged = Path.Combine(Path.GetDirectoryName(dest) ?? Folder, $".{Guid.NewGuid():N}.persocord-new");
            File.Move(newFile, staged);

            // The original goes (to the Recycle Bin if chosen). Something that was
            // just playing it (the clip player, a preview) can take a moment to
            // let go, so this tries for a few seconds before giving up.
            for (int i = 0; i < 24 && File.Exists(old); i++)
            {
                try { DeleteClipFile(old, _settings.Current.DeleteToRecycleBin); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                if (File.Exists(old)) System.Threading.Thread.Sleep(250);
            }
            if (File.Exists(old))
            {
                Log.Write($"Replace: the original is still in use by another program: {old}");
                // Handed back untouched, so the caller can keep it as a copy instead.
                try { File.Move(staged, newFile); } catch (Exception ex) { Log.Error("replace: hand back", ex); }
                return false;
            }
            File.Move(staged, dest);
            clip.FileName = Path.Combine(folder, Path.GetFileName(dest));

            var t = ThumbPathOf(clip);
            if (t != null && File.Exists(t)) File.Delete(t);

            clip.Size = new FileInfo(dest).Length;
            Recount();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("replace clip file", ex);
            return false;
        }
    }

    public static void DeleteClipFile(string path, bool toRecycleBin)
    {
        if (!File.Exists(path)) return;
        if (toRecycleBin)
        {
            // Straight to Windows, with no dialogs of its own: a busy file
            // simply fails and gets retried, instead of popping an error box.
            var op = new SHFILEOPSTRUCT
            {
                wFunc = 3,   // delete
                pFrom = path + "\0\0",
                fFlags = 0x40 | 0x10 | 0x400 | 0x4,   // allow undo, no confirmation, no error UI, silent
            };
            int rc = SHFileOperation(ref op);
            if (rc == 0 && !op.fAnyOperationsAborted && !File.Exists(path)) return;
            if (File.Exists(path) && !IsBusy(path))
            {
                // A drive without a Recycle Bin (some network and USB drives)
                // refuses; the user asked for the clip to go, so it goes.
                Log.Write($"Recycle Bin refused ({rc}); deleting directly");
                File.Delete(path);
            }
            return;
        }
        File.Delete(path);
    }

    private static bool IsBusy(string path)
    {
        try { using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch { return false; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /* ------------------------------------------------------------------ */
    /* disk accounting                                                     */
    /* ------------------------------------------------------------------ */

    private long _usedBytes = -1;
    private int _measuring;

    /// <summary>
    /// Adds up the library's size. Kept cached, because this is checked
    /// before every recording and walking the folder each time put a visible
    /// pause before the area picker appeared.
    /// </summary>
    public long Recount()
    {
        long used = 0;
        try
        {
            if (FolderExists)
                used = new DirectoryInfo(Folder)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
        }
        catch (Exception ex) { Log.Error("measure library", ex); }
        Interlocked.Exchange(ref _usedBytes, used);
        return used;
    }

    public DiskReport Disk()
    {
        var s = _settings.Current;
        long used = 0, total = 0, free = 0;

        if (FolderExists)
        {
            used = Interlocked.Read(ref _usedBytes);
            if (used < 0)
            {
                // Not measured yet (just after starting): measured in the background
                // and shown when it's done, never walked here, on the window's thread
                // (a big library made the clip list freeze as it opened).
                used = 0;
                if (Interlocked.Exchange(ref _measuring, 1) == 0)
                    _ = Task.Run(() =>
                    {
                        try { Recount(); }
                        finally { Interlocked.Exchange(ref _measuring, 0); }
                        Changed?.Invoke();
                    });
            }
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(Folder));
            if (!string.IsNullOrEmpty(root))
            {
                var d = new DriveInfo(root);
                if (d.IsReady) { total = d.TotalSize; free = d.AvailableFreeSpace; }
            }
        }
        catch (Exception ex) { Log.Error("drive info", ex); }

        long cap = (long)(total * Math.Clamp(s.MaxDiskPercent, 0.1, 90.0) / 100.0);
        long warn = (long)(cap * 0.9);   // the gauge turns red at 90% of the limit

        // The ceiling is worthless if the drive is nearly full for other
        // reasons, so it never exceeds what is actually available.
        if (free > 0 && cap > used + free) cap = used + free;

        return new DiskReport(used, cap, warn, total, free, used >= warn, used >= cap);
    }

    /* ------------------------------------------------------------------ */
    /* folder watching                                                     */
    /* ------------------------------------------------------------------ */

    private void WatchFolder()
    {
        WatchClips();
        try
        {
            _parentWatcher?.Dispose();
            _parentWatcher = null;

            var parent = Path.GetDirectoryName(Path.GetFullPath(Folder));
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return;

            // Watching the parent rather than the folder itself is the only way
            // to see the folder being renamed or deleted; a watcher on a folder
            // dies silently along with it.
            _parentWatcher = new FileSystemWatcher(parent)
            {
                NotifyFilter = NotifyFilters.DirectoryName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };

            var own = Path.GetFileName(Path.GetFullPath(Folder));

            _parentWatcher.Renamed += (_, e) =>
            {
                if (!string.Equals(e.OldName, own, StringComparison.OrdinalIgnoreCase)) return;
                AdoptNewPath(e.FullPath, "renamed");
            };

            _parentWatcher.Deleted += (_, e) =>
            {
                if (!string.Equals(e.Name, own, StringComparison.OrdinalIgnoreCase)) return;
                // A move shows up as a delete here. Give the filesystem a moment
                // to settle, then go looking before crying wolf.
                Task.Delay(1200).ContinueWith(_ => CheckFolder());
            };
        }
        catch (Exception ex) { Log.Error("watch folder", ex); }
    }

    private void AdoptNewPath(string newPath, string how)
    {
        try
        {
            if (!Directory.Exists(newPath)) return;
            if (!IsOurFolder(newPath)) return;

            _settings.Current.LibraryFolder = newPath;
            _settings.Save();

            Log.Write($"Library {how} to {newPath}");
            WatchFolder();
            Load();
            FolderRecovered?.Invoke(newPath);
        }
        catch (Exception ex) { Log.Error("adopt path", ex); }
    }

    private bool IsOurFolder(string path)
    {
        try
        {
            var marker = Path.Combine(path, MarkerFile);
            return File.Exists(marker) &&
                   File.ReadAllText(marker).Trim() == _settings.Current.LibraryId;
        }
        catch { return false; }
    }

    private bool _troubleReported;

    public void CheckFolder()
    {
        try
        {
            if (FolderExists)
            {
                if (_troubleReported)
                {
                    _troubleReported = false;
                    EnsureFolder();
                    WatchFolder();
                    Load();
                }
                return;
            }

            if (_troubleReported) return;
            _troubleReported = true;

            Log.Write($"Library folder gone: {Folder}");
            FolderTrouble?.Invoke(FolderStatus.Missing,
                $"The clips folder is not where it was:\n{Folder}\n\nLooking for it…");

            _ = SearchForFolderAsync();
        }
        catch (Exception ex) { Log.Error("check folder", ex); }
    }

    /// <summary>
    /// Hunts for the marker file across likely places. A full disk scan would
    /// take minutes, so this walks a bounded set of roots at a limited depth —
    /// which covers a folder dragged to another drive, the desktop, or a
    /// different place in the user's profile, and gives up quickly otherwise.
    /// </summary>
    public async Task<string?> SearchForFolderAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = _searchCts.Token;

        var roots = new List<(string path, int depth)>();

        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (Directory.Exists(profile)) roots.Add((profile, 5));

            var oldRoot = Path.GetPathRoot(Path.GetFullPath(Folder));
            if (!string.IsNullOrEmpty(oldRoot) && Directory.Exists(oldRoot))
                roots.Add((oldRoot, 4));

            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                if (!d.IsReady) continue;
                if (roots.Any(r => r.path.StartsWith(d.RootDirectory.FullName,
                        StringComparison.OrdinalIgnoreCase))) continue;
                roots.Add((d.RootDirectory.FullName, 3));
            }
        }
        catch (Exception ex) { Log.Error("search roots", ex); }

        string? found = null;
        await Task.Run(() =>
        {
            foreach (var (root, depth) in roots)
            {
                if (ct.IsCancellationRequested) break;
                found = Walk(root, depth, ct);
                if (found != null) break;
            }
        }, ct);

        if (found != null)
        {
            AdoptNewPath(found, "moved");
            return found;
        }

        FolderTrouble?.Invoke(FolderStatus.Missing,
            "The clips folder could not be found.\n\n" +
            "If it was moved somewhere unusual, point Persocord at it in " +
            "Settings. If it was deleted, your clips are gone and a fresh " +
            "folder will be created on the next recording.");
        return null;
    }

    private string? Walk(string dir, int depth, CancellationToken ct)
    {
        if (depth < 0 || ct.IsCancellationRequested) return null;

        try
        {
            var marker = Path.Combine(dir, MarkerFile);
            if (File.Exists(marker) &&
                File.ReadAllText(marker).Trim() == _settings.Current.LibraryId)
                return dir;
        }
        catch { /* unreadable, move on */ }

        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(dir); }
        catch { return null; }

        foreach (var child in children)
        {
            if (ct.IsCancellationRequested) return null;
            if (Skip(child)) continue;

            var hit = Walk(child, depth - 1, ct);
            if (hit != null) return hit;
        }
        return null;
    }

    private static readonly string[] Noise =
    {
        "windows", "$recycle.bin", "system volume information", "node_modules",
        "appdata\\local\\temp", "program files", "programdata", ".git",
        "onedrivetemp", "$windows.~ws",
    };

    private static bool Skip(string dir)
    {
        var lower = dir.ToLowerInvariant();
        foreach (var n in Noise) if (lower.Contains(n)) return true;

        try
        {
            var a = new DirectoryInfo(dir).Attributes;
            if (a.HasFlag(FileAttributes.ReparsePoint)) return true; // junction loops
            if (a.HasFlag(FileAttributes.System)) return true;
        }
        catch { return true; }

        return false;
    }

    /// <summary>Moves the library somewhere else, files and all.</summary>
    /// <summary>Uses another folder as it is, without moving any clips.</summary>
    public void SwitchTo(string destination)
    {
        try
        {
            var to = Path.GetFullPath(destination);
            Directory.CreateDirectory(to);
            _settings.Current.LibraryFolder = to;
            _settings.Save();
            EnsureFolder();
            WatchFolder();
            Load();
            Log.Write("Clips folder switched to " + to + " (nothing moved)");
        }
        catch (Exception ex) { Log.Error("switch clips folder", ex); }
    }

    public async Task<bool> MoveToAsync(string destination)
    {
        try
        {
            var from = Path.GetFullPath(Folder);
            var to = Path.GetFullPath(destination);
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return true;

            Directory.CreateDirectory(to);

            await Task.Run(() =>
            {
                // Subfolders come along with their contents, so the folder
                // layout survives the move. A move across drives is a copy
                // underneath, so this can take a while on a large library;
                // it runs off the UI thread.
                foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).ToList())
                {
                    var target = Path.Combine(to, Path.GetRelativePath(from, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, true);
                    File.Delete(file);
                }
                // Empty folders left behind go, deepest first; a folder that
                // somehow still has something in it is left alone.
                foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)
                                             .OrderByDescending(d => d.Length).ToList())
                {
                    Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
                    try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
                }
                try { if (!Directory.EnumerateFileSystemEntries(from).Any()) Directory.Delete(from); }
                catch { }
            });

            _settings.Current.LibraryFolder = to;
            _settings.Save();

            EnsureFolder();
            WatchFolder();
            Load();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("move library", ex);
            return false;
        }
    }
}
