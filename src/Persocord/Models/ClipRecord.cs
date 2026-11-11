using System.Collections.Generic;
using System;
using System.IO;
using System.Text.Json.Serialization;

namespace Persocord.Models;

public enum ClipState { Ready, Processing, Missing }

/// <summary>One recorded clip on disk.</summary>
public class ClipRecord
{
    /// <summary>Stable across renames and folder moves; the file name is not.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Title { get; set; } = "";
    public string App { get; set; } = "";

    /// <summary>Relative to the library folder, so the whole folder can move.</summary>
    public string FileName { get; set; } = "";

    /// <summary>Thumbnail file name, also relative to the library folder.</summary>
    public string? ThumbName { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; }
    public long DurationMs { get; set; }
    public long Size { get; set; }

    public bool Favourite { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;

    public ClipState State { get; set; } = ClipState.Ready;
    public string? Note { get; set; }

    /// <summary>Labels the user adds to organise clips.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Came from outside Persocord: copied in, or another recorder's file.</summary>
    [JsonIgnore] public bool IsImported => Note is "Found in the folder" or "Imported";

    /// <summary>Made in the editor or by joining clips.</summary>
    [JsonIgnore] public bool IsEdited =>
        Note != null && (Note.StartsWith("Edited", StringComparison.Ordinal) || Note.StartsWith("Joined", StringComparison.Ordinal));

    /// <summary>Private clips can never be uploaded as a link.</summary>
    public bool Private { get; set; }

    /// <summary>The clip's current share link, if it has one.</summary>
    public ClipLink? Link { get; set; }

    /// <summary>Set when a shrunk copy was made, pointing at the full-size original.</summary>
    public string? OriginalFileName { get; set; }

    [JsonIgnore] public string Ext => Path.GetExtension(FileName).TrimStart('.').ToLowerInvariant();
    [JsonIgnore] public bool IsGif => Ext == "gif";

    [JsonIgnore]
    public string DurationText
    {
        get
        {
            var t = (int)Math.Round(DurationMs / 1000.0);
            return $"{t / 60}:{t % 60:00}";
        }
    }

    [JsonIgnore]
    public string SizeText => Size switch
    {
        <= 0 => "—",
        < 1024 * 1024 => $"{Size / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{Size / 1048576.0:0.0} MB",
        _ => $"{Size / 1073741824.0:0.00} GB",
    };
}

/// <summary>A share link made for a clip.</summary>
public class ClipLink
{
    public string Url { get; set; } = "";
    public string Service { get; set; } = "";
    public string DurationKey { get; set; } = "";
    public DateTime Uploaded { get; set; } = DateTime.Now;

    /// <summary>When the host deletes it. Null means it stays up for good.</summary>
    public DateTime? Expires { get; set; }

    /// <summary>
    /// Lets the link be taken down early, where the host offers that: a
    /// the management page address, for Oshi.
    /// </summary>
    public string? DeleteToken { get; set; }

    /// <summary>The file's own address, when the copied link is an embed page.</summary>
    public string? RawUrl { get; set; }

    /// <summary>The preview image uploaded alongside, for embed pages.</summary>
    public string? ThumbUrl { get; set; }
    public string? ThumbDeleteToken { get; set; }

    /// <summary>True when the clip was shrunk to play inline before uploading.</summary>
    public bool Shrunk { get; set; }

    [JsonIgnore] public bool Expired => Expires.HasValue && Expires.Value <= DateTime.Now;
    [JsonIgnore] public bool CanKill => !string.IsNullOrEmpty(DeleteToken);

    [JsonIgnore]
    public string TimeLeftText
    {
        get
        {
            if (!Expires.HasValue) return "long-term";
            var left = Expires.Value - DateTime.Now;
            if (left <= TimeSpan.Zero) return "expired";
            if (left.TotalDays >= 2) return $"{(int)left.TotalDays} days left";
            if (left.TotalHours >= 1) return $"{(int)left.TotalHours}h {left.Minutes}m left";
            return $"{Math.Max(1, left.Minutes)}m left";
        }
    }
}
