using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WindowsDaSiTool.Services;

/// <summary>
/// What the taskbar shows right now - through UI Automation, like a screen
/// reader. Every button of a pinned or running program carries the
/// AutomationId "Appid: &lt;ID&gt;" (language independent, unlike its name
/// "Microsoft Edge pinned" / "... angeheftet").
///
/// Reason: after a restore the taskbar was empty while the log reported
/// success - Windows had not shown the restored entries. No registry value
/// says whether Windows shows them.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TaskleistenAnsicht
{
    /// <summary>
    /// The app IDs of the program buttons on the taskbar. null if the taskbar
    /// cannot be read (no taskbar, lock screen in front, no buttons found).
    /// </summary>
    public static HashSet<string>? AppIds()
    {
        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return null;
        try
        {
            var uia = (IUIAutomation)new CUIAutomation();
            try
            {
                uia.ElementFromHandle(tray, out var taskleiste);
                uia.CreateTrueCondition(out var alle);
                taskleiste.FindAll(4 /* TreeScope_Descendants */, alle, out var gefunden);
                gefunden.get_Length(out var anzahl);
                var erg = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < anzahl; i++)
                {
                    gefunden.GetElement(i, out var el);
                    el.get_CurrentAutomationId(out var id);
                    if (id is not null && id.StartsWith("Appid: ", StringComparison.OrdinalIgnoreCase))
                        erg.Add(id["Appid: ".Length..].Trim());
                }
                return erg.Count > 0 ? erg : null;
            }
            finally { Marshal.ReleaseComObject(uia); }
        }
        catch { return null; }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    // UI Automation (UIAutomationClient.h). The methods are in the interface's
    // order - unused ones are only placeholders.

    [ComImport, Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E")]
    private class CUIAutomation { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
    private interface IUIAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        void ElementFromHandle(IntPtr hwnd, out IUIAutomationElement element);
        void ElementFromPoint();
        void GetFocusedElement();
        void GetRootElementBuildCache();
        void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache();
        void GetFocusedElementBuildCache();
        void CreateTreeWalker();
        void get_ControlViewWalker();
        void get_ContentViewWalker();
        void get_RawViewWalker();
        void get_RawViewCondition();
        void get_ControlViewCondition();
        void get_ContentViewCondition();
        void CreateCacheRequest();
        void CreateTrueCondition([MarshalAs(UnmanagedType.IUnknown)] out object condition);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        void FindFirst();
        void FindAll(int scope, [MarshalAs(UnmanagedType.IUnknown)] object condition, out IUIAutomationElementArray found);
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        void GetCurrentPropertyValue();
        void GetCurrentPropertyValueEx();
        void GetCachedPropertyValue();
        void GetCachedPropertyValueEx();
        void GetCurrentPatternAs();
        void GetCachedPatternAs();
        void GetCurrentPattern();
        void GetCachedPattern();
        void GetCachedParent();
        void GetCachedChildren();
        void get_CurrentProcessId();
        void get_CurrentControlType();
        void get_CurrentLocalizedControlType();
        void get_CurrentName();
        void get_CurrentAcceleratorKey();
        void get_CurrentAccessKey();
        void get_CurrentHasKeyboardFocus();
        void get_CurrentIsKeyboardFocusable();
        void get_CurrentIsEnabled();
        void get_CurrentAutomationId([MarshalAs(UnmanagedType.BStr)] out string id);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("14314595-B4BC-4055-95F2-58F2E42C9855")]
    private interface IUIAutomationElementArray
    {
        void get_Length(out int length);
        void GetElement(int index, out IUIAutomationElement element);
    }
}
