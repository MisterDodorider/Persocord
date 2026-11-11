using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Persocord.Services;

/// <summary>
/// Gives Persocord's own shortcuts (the taskbar pin, Start menu, desktop) the
/// logo in its chosen colours. Windows draws a pinned app with its shortcut's
/// icon, so this is the only way a pin can match.
///
/// Only shortcuts that open this very Persocord.exe are touched, and only
/// their icon. Nothing is sent anywhere, other shortcuts are read only to
/// see where they point (nothing about them is kept or logged), and the
/// .exe itself is never changed.
/// </summary>
public static class ShortcutIcons
{
    private static readonly object Gate = new();

    /// <summary>Where the coloured icons are kept (one at a time).</summary>
    public static string Folder => Path.Combine(Paths.Root, "logo");

    /// <summary>
    /// Points every Persocord shortcut at the icon: a file made from
    /// <paramref name="ico"/> (named by <paramref name="key"/>, so Windows
    /// sees a new picture and doesn't keep its cached one), or, with null,
    /// back at the .exe's own icon. Returns how many shortcuts changed.
    /// </summary>
    public static int Apply(byte[]? ico, string key)
    {
        lock (Gate)
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return 0;
            exe = Path.GetFullPath(exe);

            string iconFile = exe;
            if (ico != null)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    iconFile = Path.Combine(Folder, $"persocord-{Safe(key)}.ico");
                    if (!File.Exists(iconFile) || new FileInfo(iconFile).Length != ico.Length)
                    {
                        File.WriteAllBytes(iconFile + ".part", ico);
                        File.Move(iconFile + ".part", iconFile, true);
                    }
                }
                catch (Exception ex) { Log.Error("shortcut icon file", ex); return 0; }
            }

            int changed = 0;
            foreach (var lnk in Candidates())
            {
                try { if (Point(lnk, exe, iconFile)) changed++; }
                catch { /* a shortcut that can't be read or written (another user's, read-only) is left alone */ }
            }
            if (changed > 0)
            {
                // Tells Explorer the pictures changed, so the taskbar redraws without a restart.
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
                Log.Write($"Logo: {changed} Persocord shortcut(s) now use the {(ico == null ? "original" : "chosen")} colours.");
            }

            // Older coloured icons nothing uses any more.
            try
            {
                if (Directory.Exists(Folder))
                    foreach (var f in Directory.GetFiles(Folder, "persocord-*.ico*"))
                        if (!string.Equals(f, iconFile, StringComparison.OrdinalIgnoreCase))
                            try { File.Delete(f); } catch { }
            }
            catch { }
            return changed;
        }
    }

    private static string Safe(string key) => new(key.Where(char.IsLetterOrDigit).Take(40).ToArray());

    /// <summary>The places Windows keeps shortcuts a person uses to open an app.</summary>
    private static IEnumerable<string> Candidates()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var places = new List<(string Dir, bool Deep)>
        {
            (Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"), false),
            (Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch"), false),
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false),
            (Environment.GetFolderPath(Environment.SpecialFolder.Programs), true),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), false),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), true),
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, deep) in places)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.lnk", new EnumerationOptions
                {
                    RecurseSubdirectories = deep, MaxRecursionDepth = 3, IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
                }).ToList();
            }
            catch { continue; }
            foreach (var f in files)
                if (seen.Add(f)) yield return f;
        }
    }

    /// <summary>Gives one shortcut the icon, if it opens this Persocord and doesn't have it already.</summary>
    private static bool Point(string lnk, string exe, string iconFile)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            var file = (IPersistFile)link;
            file.Load(lnk, 0);   // STGM_READ

            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, SLGP_RAWPATH);
            var t = Environment.ExpandEnvironmentVariables(target.ToString());
            if (t.Length == 0) return false;
            try { t = Path.GetFullPath(t); } catch { return false; }
            if (!string.Equals(t, exe, StringComparison.OrdinalIgnoreCase)) return false;

            var icon = new StringBuilder(1024);
            link.GetIconLocation(icon, icon.Capacity, out int index);
            var now = Environment.ExpandEnvironmentVariables(icon.ToString());
            // No icon set means "the target's own", which is the .exe's.
            bool same = now.Length == 0
                ? string.Equals(iconFile, exe, StringComparison.OrdinalIgnoreCase)
                : string.Equals(now, iconFile, StringComparison.OrdinalIgnoreCase) && index == 0;
            if (same) return false;

            link.SetIconLocation(iconFile, 0);
            file.Save(lnk, true);
            var name = Marshal.StringToHGlobalUni(lnk);
            try { SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, name, IntPtr.Zero); }
            finally { Marshal.FreeHGlobal(name); }
            return true;
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    /* ---- the Windows shell's shortcut object ------------------------------------- */

    private const uint SLGP_RAWPATH = 0x4;
    private const int SHCNE_UPDATEITEM = 0x00002000, SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000, SHCNF_PATHW = 0x0005;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid id);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }
}
