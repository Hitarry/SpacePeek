using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SpacePeek.Interop;

/// <summary>
/// Pulls the shell's cached "page 1" image for a document. This is what Explorer's large-icon
/// view uses; it is orders of magnitude faster than instantiating a preview handler for a
/// long document, which has to lay out every page.
/// </summary>
internal static class ShellThumbnail
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, uint flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IntPtr ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const uint SIIGBF_BIGGERSIZEOK = 0x01;
    private const uint SIIGBF_THUMBNAILONLY = 0x08;

    /// <summary>Returns null when the shell cannot produce a real thumbnail.</summary>
    public static BitmapSource? TryGet(string path, int size)
    {
        IntPtr factoryPtr = IntPtr.Zero;
        try
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factoryPtr);
            if (hr != 0 || factoryPtr == IntPtr.Zero) return null;

            var factory = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(factoryPtr);
            var sz = new SIZE { cx = size, cy = size };
            hr = factory.GetImage(sz, SIIGBF_BIGGERSIZEOK | SIIGBF_THUMBNAILONLY, out IntPtr hbm);
            if (hr != 0 || hbm == IntPtr.Zero) return null;

            try
            {
                BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                    hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                // an icon-sized result means "no thumbnail", not a page image
                if (source.PixelWidth < 160) return null;
                return source;
            }
            finally
            {
                DeleteObject(hbm);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (factoryPtr != IntPtr.Zero) Marshal.Release(factoryPtr);
        }
    }
}
