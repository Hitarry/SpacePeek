using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace SpacePeek;

internal enum TriggerMode
{
    Space,
    Hotkey,
}

internal sealed class Settings
{
    public TriggerMode TriggerMode { get; set; } = TriggerMode.Space;
    public bool StartWithWindows { get; set; }

    private static readonly string Directory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpacePeek");

    private static readonly string FilePath = Path.Combine(Directory, "config.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Settings? loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json);
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception ex) { Log.Exception("settings-load", ex); }

        return new Settings();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { Log.Exception("settings-save", ex); }
    }

    // ------------------------------------------------------------ autostart
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "SpacePeek";

    public static bool IsStartupEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string value && value.Length > 0;
        }
        catch { return false; }
    }

    public static bool SetStartup(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, true)!;
            if (!enabled)
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
                return true;
            }

            string? exe = Environment.ProcessPath;
            if (exe is null) return false;

            key.SetValue(RunValue, $"\"{exe}\"");
            return true;
        }
        catch (Exception ex)
        {
            Log.Exception("autostart", ex);
            return false;
        }
    }

    public static void ClearCache()
    {
        try
        {
            string cache = Path.Combine(Directory, "cache");
            if (!System.IO.Directory.Exists(cache)) return;
            foreach (string file in System.IO.Directory.GetFiles(cache)) File.Delete(file);
            Log.Write("cache cleared");
        }
        catch (Exception ex) { Log.Exception("cache-clear", ex); }
    }
}
