using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SpacePeek.Interop;

/// <summary>Minimal COM surface for hosting a Windows shell preview handler.</summary>
internal static class PreviewHandlerInterop
{
    public const string PreviewHandlerKey = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    [StructLayout(LayoutKind.Sequential)]
    public struct RECTI
    {
        public int Left, Top, Right, Bottom;
    }

    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPreviewHandler
    {
        void SetWindow(IntPtr hwndParent, ref RECTI rect);
        void SetRect(ref RECTI rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr phwnd);
        [PreserveSig] uint TranslateAccelerator(ref IntPtr pmsg);
    }

    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string filePath, uint grfMode);
    }

    [ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInitializeWithItem
    {
        void Initialize(IntPtr shellItem, uint grfMode);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IntPtr ppv);

    /// <summary>Finds the preview handler registered for this file's extension.</summary>
    public static Guid? ResolveClsid(string extension)
    {
        string[] candidates =
        {
            $@"{extension}\shellex\{PreviewHandlerKey}",
            $@"SystemFileAssociations\{extension}\shellex\{PreviewHandlerKey}",
        };

        foreach (string sub in candidates)
        {
            try
            {
                using RegistryKey? key = Registry.ClassesRoot.OpenSubKey(sub);
                if (key?.GetValue(null) is string raw && Guid.TryParse(raw, out Guid g)) return g;
            }
            catch { }
        }

        // fall back through the extension's ProgID
        try
        {
            using RegistryKey? ext = Registry.ClassesRoot.OpenSubKey(extension);
            if (ext?.GetValue(null) is string progId && progId.Length > 0)
            {
                using RegistryKey? key = Registry.ClassesRoot.OpenSubKey($@"{progId}\shellex\{PreviewHandlerKey}");
                if (key?.GetValue(null) is string raw && Guid.TryParse(raw, out Guid g)) return g;
            }
        }
        catch { }

        return null;
    }

    /// <summary>Creates a preview handler for <paramref name="path"/> and initialises it.</summary>
    public static IPreviewHandler Create(string path)
    {
        string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        Guid? clsid = ResolveClsid(extension)
                      ?? throw new InvalidOperationException("没有为此类型注册预览组件");

        Type type = Type.GetTypeFromCLSID(clsid.Value, throwOnError: true)!;
        object instance = Activator.CreateInstance(type)!;
        var handler = (IPreviewHandler)instance;

        if (instance is IInitializeWithFile withFile)
        {
            withFile.Initialize(path, 0);
            return handler;
        }

        if (instance is IInitializeWithItem withItem)
        {
            Guid iid = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IntPtr psi);
            if (hr != 0) throw new InvalidOperationException($"SHCreateItemFromParsingName 0x{hr:X8}");
            try { withItem.Initialize(psi, 0); }
            finally { if (psi != IntPtr.Zero) Marshal.Release(psi); }
            return handler;
        }

        Marshal.ReleaseComObject(instance);
        throw new InvalidOperationException("预览组件不支持该初始化方式");
    }
}
