using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SpacePeek;

/// <summary>Loads the exe's own icon so the tray, the HUD and the taskbar stay consistent.</summary>
internal static class AppIcon
{
    public static Icon? LoadDrawingIcon()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is not null)
            {
                Icon? extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null) return extracted;
            }
        }
        catch { }
        return SystemIcons.Application;
    }

    public static BitmapSource? LoadWpfIcon()
    {
        try
        {
            using Icon? icon = LoadDrawingIcon();
            if (icon is null) return null;

            BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch { return null; }
    }
}
