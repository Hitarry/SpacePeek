using System;
using System.Collections.Generic;
using System.Windows.Automation;
using System.Windows;
using SpacePeek.Interop;

namespace SpacePeek.Shell;

/// <summary>Locates the selected Explorer item's screen rectangle through UI Automation.</summary>
internal static class UiaProbe
{
    private static AutomationElement? _listCache;
    private static IntPtr _listHwnd;
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(120);

    public static Rect? TryGetItemRect(IntPtr explorerHwnd, string itemName)
    {
        try
        {
            AutomationElement? list = GetList(explorerHwnd);
            if (list is null) return null;

            var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
            AutomationElementCollection kids = list.FindAll(TreeScope.Children, cond);
            foreach (AutomationElement el in kids)
            {
                if (!string.Equals(el.Current.Name, itemName, StringComparison.Ordinal)) continue;
                System.Windows.Rect r = el.Current.BoundingRectangle;
                if (r.IsEmpty) continue;
                return r;
            }
            _listCache = null; // name not found: the view may have changed
        }
        catch
        {
            _listCache = null;
        }
        return null;
    }

    public static void Invalidate()
    {
        _listCache = null;
        _listHwnd = IntPtr.Zero;
    }

    private static AutomationElement? GetList(IntPtr hwnd)
    {
        if (_listCache is not null && _listHwnd == hwnd) return _listCache;

        AutomationElement root = AutomationElement.FromHandle(hwnd);
        var cond = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataGrid));
        AutomationElement? list = root.FindFirst(TreeScope.Descendants, cond);
        if (list is not null)
        {
            _listCache = list;
            _listHwnd = hwnd;
        }
        return list;
    }

    /// <summary>Item names currently exposed by UIA (used to disambiguate Explorer tabs).</summary>
    public static IReadOnlyList<string> VisibleItemNames(IntPtr explorerHwnd)
    {
        var names = new List<string>();
        try
        {
            AutomationElement? list = GetList(explorerHwnd);
            if (list is null) return names;
            var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
            foreach (AutomationElement el in list.FindAll(TreeScope.Children, cond))
                names.Add(el.Current.Name);
        }
        catch { }
        return names;
    }
}
