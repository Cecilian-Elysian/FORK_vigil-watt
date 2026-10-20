using System.Runtime.InteropServices;
using System.Text;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 任务栏条带上的第三方占位判定（窗口枚举），系统 XAML UI 的矩形由 TaskbarXamlRects 供、调用方合并。
/// 2026/10/20 15:20:00 xliu
/// Win11 任务栏系统 UI 无独立子窗口、GDI 采样全黑，枚举与像素两条路都看不见它，见 runtime-pitfalls.md §16.11。
/// </summary>
public static class TaskbarOccupancy
{
    /// <summary>系统自己的窗口：不算工具占位（托盘是锚点，更不能当障碍）。</summary>
    private static readonly HashSet<string> SystemClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "TrayNotifyWnd",
        "TrayDummySearchControl",
        "ReBarWindow32",
        "Windows.UI.Core.CoreWindow",
        "Windows.UI.Composition.DesktopWindowContentBridge",
        "Windows.UI.Input.InputSite.WindowClass",
        "Shell_TrayWnd",
    };

    /// <summary>
    /// 收集第三方占位矩形（屏幕坐标）：任务栏子窗口 + 压在任务栏条带上的顶层浮窗。
    /// 系统 XAML UI（搜索框/任务按钮/托盘）枚举看不见或矩形失真，由调用方并入 TaskbarXamlRects 的结果。
    /// 自己的窗口、不可见窗口、DWM 隐藏的壳一律排除。
    /// </summary>
    public static List<PixelRect> Collect(PixelRect taskbar, IntPtr taskbarHwnd, IntPtr selfHwnd, int selfPid,
        List<PixelRect>? fullWindows = null)
    {
        var list = new List<PixelRect>();
        var seen = new HashSet<IntPtr>();

        Native.EnumWindowsProc childProc = (h, _) =>
        {
            if (h == selfHwnd || !seen.Add(h)) return true;
            if (SystemClasses.Contains(ClassName(h))) return true;
            if (!Native.IsWindowVisible(h)) return true;
            if (TryRect(h, out var rect) && OccupiesBand(rect, taskbar))
            {
                fullWindows?.Add(rect);
                list.Add(rect);
            }
            return true;
        };
        Native.EnumChildWindows(taskbarHwnd, childProc, IntPtr.Zero);
        GC.KeepAlive(childProc);

        Native.EnumWindowsProc topProc = (h, _) =>
        {
            if (h == selfHwnd || !seen.Add(h)) return true;
            if (Native.GetAncestor(h, Native.GA_ROOT) == taskbarHwnd) return true;
            if (!Native.IsWindowVisible(h) || IsCloaked(h)) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == (uint)selfPid) return true;
            if (!TryRect(h, out var rect) || !OccupiesBand(rect, taskbar)) return true;
            // 浮窗得"贴着任务栏"才算占位：开始菜单、输入法候选窗都是大块浮窗，不该挤走贴片
            if (rect.Height > taskbar.Height * 2) return true;
            int centerY = rect.Top + rect.Height / 2;
            if (centerY < taskbar.Top || centerY > taskbar.Bottom) return true;
            fullWindows?.Add(rect);
            list.Add(rect);
            return true;
        };
        Native.EnumWindows(topProc, IntPtr.Zero);
        GC.KeepAlive(topProc);

        return list;
    }

    /// <summary>贴片横放时按竖向重叠判、竖放时按横向重叠判：只擦到边不算占位。</summary>
    private static bool OccupiesBand(PixelRect rect, PixelRect taskbar)
    {
        bool horizontal = taskbar.Width >= taskbar.Height;
        int thickness = horizontal ? taskbar.Height : taskbar.Width;
        int along = horizontal
            ? Math.Min(rect.Bottom, taskbar.Bottom) - Math.Max(rect.Top, taskbar.Top)
            : Math.Min(rect.Right, taskbar.Right) - Math.Max(rect.Left, taskbar.Left);
        if (along < thickness / 2) return false;

        int across = horizontal
            ? Math.Min(rect.Right, taskbar.Right) - Math.Max(rect.Left, taskbar.Left)
            : Math.Min(rect.Bottom, taskbar.Bottom) - Math.Max(rect.Top, taskbar.Top);
        return across > 0;
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return Native.GetClassNameW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    private static bool TryRect(IntPtr hwnd, out PixelRect rect)
    {
        rect = default;
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        rect = new PixelRect(r.Left, r.Top, r.Width, r.Height);
        return true;
    }

    /// <summary>DWM 隐藏标记：UWP/XAML 宿主会留一个不可见但有尺寸的壳，不排除就等于整条任务栏被占。</summary>
    private static bool IsCloaked(IntPtr hwnd)
        => Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0;
}
