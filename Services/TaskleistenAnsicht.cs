using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Was die Taskleiste gerade zeigt - ueber UI Automation, wie ein
/// Bildschirmleser. Jeder Knopf eines angehefteten oder laufenden Programms
/// traegt als AutomationId "Appid: &lt;Kennung&gt;" (sprachunabhaengig, anders als
/// sein Name "Microsoft Edge pinned" / "... angeheftet").
///
/// Anlass: Nach einer Wiederherstellung war die Taskleiste leer, das Protokoll
/// meldete Erfolg - Windows hatte die zurueckgeschriebenen Eintraege nicht
/// angezeigt. Ob Windows sie zeigt, steht in keinem Registry-Wert.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TaskleistenAnsicht
{
    /// <summary>
    /// Die App-Kennungen der Programmknoepfe auf der Taskleiste. null, wenn
    /// sich die Taskleiste nicht lesen laesst (keine Taskleiste, Sperrbildschirm
    /// davor, keine Knoepfe gefunden).
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

    // UI Automation (UIAutomationClient.h). Die Reihenfolge der Methoden ist die
    // der Schnittstelle - nicht benutzte stehen nur als Platzhalter da.

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
