using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Rectangle = System.Drawing.Rectangle;

namespace Persocord.Services;

/// <summary>A monitor as the graphics driver numbers it.</summary>
public record DxgiTarget(int Adapter, int Output, Rectangle Desktop, uint VendorId, string DeviceName)
{
    public bool IsNvidia => VendorId == 0x10DE;

    /// <summary>The graphics card's own video memory, in bytes.</summary>
    public ulong VideoMemoryBytes { get; init; }
}

/// <summary>
/// Finds which GPU and which output on that GPU shows a given screen area.
///
/// FFmpeg's fast capture addresses a monitor as "output N of adapter M", in
/// the order DirectX enumerates them. That order has no guaranteed relation
/// to how Windows numbers displays, and outputs are counted per GPU, so a
/// monitor plugged into the motherboard and one plugged into the graphics
/// card can both be "output 0". Guessing from Windows' numbering is what
/// made a selection on one monitor record the other.
///
/// The answer comes from asking DirectX directly: enumerate every adapter
/// and output and match on desktop coordinates.
/// </summary>
public static class DxgiOutputs
{
    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OutputDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public RECT DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumFn(IntPtr self, uint index, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OutputDescFn(IntPtr self, out OutputDesc desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AdapterDescFn(IntPtr self, out AdapterDesc desc);

    // Positions in each interface's method table. These are fixed by the
    // DirectX ABI: IUnknown takes 0-2 and IDXGIObject 3-6, so each
    // interface's own methods start at 7.
    private const int FactoryEnumAdapters = 7;
    private const int AdapterEnumOutputs = 7;
    private const int AdapterGetDesc = 8;
    private const int OutputGetDesc = 7;

    private static T Method<T>(IntPtr com, int slot) where T : Delegate
    {
        var table = Marshal.ReadIntPtr(com);
        var fn = Marshal.ReadIntPtr(table, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    public static List<DxgiTarget> All()
    {
        var found = new List<DxgiTarget>();
        IntPtr factory = IntPtr.Zero;

        try
        {
            var iid = IidFactory1;
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero)
                return found;

            var enumAdapters = Method<EnumFn>(factory, FactoryEnumAdapters);

            for (uint a = 0; a < 16; a++)
            {
                if (enumAdapters(factory, a, out var adapter) < 0 || adapter == IntPtr.Zero) break;
                try
                {
                    uint vendor = 0;
                    ulong vram = 0;
                    if (Method<AdapterDescFn>(adapter, AdapterGetDesc)(adapter, out var ad) >= 0)
                    {
                        vendor = ad.VendorId;
                        vram = (ulong)ad.DedicatedVideoMemory;
                    }

                    var enumOutputs = Method<EnumFn>(adapter, AdapterEnumOutputs);
                    for (uint o = 0; o < 16; o++)
                    {
                        if (enumOutputs(adapter, o, out var output) < 0 || output == IntPtr.Zero) break;
                        try
                        {
                            if (Method<OutputDescFn>(output, OutputGetDesc)(output, out var d) >= 0 &&
                                d.AttachedToDesktop != 0)
                            {
                                var r = d.DesktopCoordinates;
                                // The raw loop index is kept deliberately: FFmpeg
                                // counts outputs the same way, attached or not.
                                found.Add(new DxgiTarget(
                                    (int)a, (int)o,
                                    new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
                                    vendor, d.DeviceName ?? "") { VideoMemoryBytes = vram });
                            }
                        }
                        finally { Marshal.Release(output); }
                    }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        catch (Exception ex) { Log.Error("dxgi enumerate", ex); }
        finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }

        return found;
    }

    /// <summary>The makers of every graphics chip in the PC (by vendor id), in Windows' order.</summary>
    public static List<uint> AdapterVendors()
    {
        var found = new List<uint>();
        IntPtr factory = IntPtr.Zero;
        try
        {
            var iid = IidFactory1;
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero) return found;
            var enumAdapters = Method<EnumFn>(factory, FactoryEnumAdapters);
            for (uint a = 0; a < 16; a++)
            {
                if (enumAdapters(factory, a, out var adapter) < 0 || adapter == IntPtr.Zero) break;
                try
                {
                    if (Method<AdapterDescFn>(adapter, AdapterGetDesc)(adapter, out var ad) >= 0 &&
                        ad.VendorId != 0 && ad.VendorId != 0x1414 && !found.Contains(ad.VendorId))
                        found.Add(ad.VendorId);   // 0x1414: Windows' own software renderer
                }
                finally { Marshal.Release(adapter); }
            }
        }
        catch (Exception ex) { Log.Error("dxgi adapters", ex); }
        finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }
        return found;
    }

    /// <summary>The output showing the centre of the given area, or null.</summary>
    public static DxgiTarget? Locate(Rectangle region)
    {
        var centre = new System.Drawing.Point(
            region.X + region.Width / 2, region.Y + region.Height / 2);

        var all = All();
        var hit = all.FirstOrDefault(t => t.Desktop.Contains(centre));

        Log.Write("DXGI outputs: " + string.Join("; ",
            all.Select(t => $"adapter {t.Adapter} output {t.Output} {t.DeviceName} " +
                            $"{t.Desktop.X},{t.Desktop.Y} {t.Desktop.Width}x{t.Desktop.Height}")) +
                  (hit == null ? "  -> no match" : $"  -> using adapter {hit.Adapter} output {hit.Output}"));

        return hit;
    }
}
