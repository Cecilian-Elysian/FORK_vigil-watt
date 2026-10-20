using System.Runtime.InteropServices;
using System.Text;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 任务栏条带上的第三方占位判定（窗口枚举 + 内容收窄），系统 XAML UI 的矩形由 TaskbarXamlRects 供、调用方合并。
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
        Native.GetWindowThreadProcessId(taskbarHwnd, out uint shellPid);
        PixelRect? selfRect = TryRect(selfHwnd, out var sr) ? sr : null;

        Native.EnumWindowsProc childProc = (h, _) =>
        {
            if (h == selfHwnd || !seen.Add(h)) return true;
            if (SystemClasses.Contains(ClassName(h))) return true;
            if (!Native.IsWindowVisible(h)) return true;
            if (TryRect(h, out var rect) && OccupiesBand(rect, taskbar))
            {
                fullWindows?.Add(rect);
                list.Add(Refine(h, rect, taskbar, shellPid, selfRect));
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
            list.Add(Refine(h, rect, taskbar, shellPid, selfRect));
            return true;
        };
        Native.EnumWindows(topProc, IntPtr.Zero);
        GC.KeepAlive(topProc);

        return list;
    }

    /// <summary>第三方窗（酷狗会把歌词窗嵌成任务栏子窗口，顶层/子窗口两条路都得采样）按内容左缘收窄占位。
    /// explorer 自家窗口不采：任务按钮之间有透明缝，收窄了贴片会钻进按钮空当。</summary>
    private static PixelRect Refine(IntPtr hwnd, PixelRect rect, PixelRect taskbar, uint shellPid, PixelRect? selfRect)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == shellPid) return rect;
        return TaskbarLayout.RefineByContent(rect, LeftmostContentX(rect, taskbar, selfRect));
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

    /// <summary>截一块屏幕到 BGRA 数组。只用于第三方窗采样（任务栏 XAML 表面采到的是全黑，见 §16.11）。失败返回 false。</summary>
    private static bool TryCapture(PixelRect region, out int[] pixels)
    {
        pixels = Array.Empty<int>();
        try
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return false;
            try
            {
                var bmi = new Native.BITMAPINFO();
                bmi.bmiHeader.biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>();
                bmi.bmiHeader.biWidth = region.Width;
                bmi.bmiHeader.biHeight = -region.Height; // 负数 = top-down，行序与 y 一致
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;

                IntPtr memDc = Native.CreateCompatibleDC(screen);
                if (memDc == IntPtr.Zero) return false;
                try
                {
                    IntPtr bits;
                    IntPtr dib = Native.CreateDIBSection(memDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
                    if (dib == IntPtr.Zero || bits == IntPtr.Zero) return false;
                    try
                    {
                        IntPtr old = Native.SelectObject(memDc, dib);
                        bool ok = Native.BitBlt(memDc, 0, 0, region.Width, region.Height, screen, region.Left, region.Top, Native.SRCCOPY);
                        Native.SelectObject(memDc, old);
                        if (!ok) return false;

                        pixels = new int[region.Width * region.Height];
                        Marshal.Copy(bits, pixels, 0, pixels.Length);
                        return true;
                    }
                    finally { Native.DeleteObject(dib); }
                }
                finally { Native.DeleteDC(memDc); }
            }
            finally { Native.ReleaseDC(IntPtr.Zero, screen); }
        }
        catch
        {
            pixels = Array.Empty<int>();
            return false;
        }
    }

    /// <summary>
    /// 浮窗在条带里画了内容的最左一列屏幕 x；全空/失败返回 null（调用方回退窗口矩形）。
    /// <paramref name="selfRect"/> 是贴片自己的矩形：贴片贴进浮窗空白区后，它的亮色像素会被
    /// 一起采回来，内容左缘误判到贴片边上——占位变宽贴片退、退出去又贴回来，来回闪。
    /// </summary>
    private static int? LeftmostContentX(PixelRect windowRect, PixelRect taskbar, PixelRect? selfRect)
    {
        int top = Math.Max(windowRect.Top, taskbar.Top) + 6;
        int height = Math.Min(windowRect.Bottom, taskbar.Bottom) - 6 - top;
        int width = windowRect.Width;
        if (width <= 0 || height <= 0) return null;
        if (!TryCapture(new PixelRect(windowRect.Left, top, width, height), out var pixels)) return null;

        var bg = MajorityBackground(pixels);
        for (int x = 0; x < width; x++)
        {
            // 贴片自己盖住的列跳过：那里读到的是贴片不是浮窗内容
            int screenX = windowRect.Left + x;
            if (selfRect is PixelRect s && screenX >= s.Left && screenX < s.Right) continue;
            for (int y = 0; y < height; y++)
            {
                if (DiffersFromBackground(pixels[y * width + x], bg)) return windowRect.Left + x;
            }
        }
        return null;
    }

    /// <summary>采样区域的众数背景色（5 位/通道量化统计），细笔画文字赢不了实心背景。</summary>
    private static (int B, int G, int R) MajorityBackground(int[] pixels)
    {
        var counts = new Dictionary<int, int>();
        foreach (var px in pixels)
        {
            int key = Quantize(px);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        int best = 0, bestCount = -1;
        foreach (var kv in counts)
        {
            if (kv.Value > bestCount) { bestCount = kv.Value; best = kv.Key; }
        }
        return ((best & 0x1F) << 3, ((best >> 5) & 0x1F) << 3, ((best >> 10) & 0x1F) << 3);
    }

    private static bool DiffersFromBackground(int bgra, (int B, int G, int R) bg)
    {
        int dB = (bgra & 0xFF) - bg.B, dG = ((bgra >> 8) & 0xFF) - bg.G, dR = ((bgra >> 16) & 0xFF) - bg.R;
        return Math.Abs(dB) > 20 || Math.Abs(dG) > 20 || Math.Abs(dR) > 20;
    }

    /// <summary>BGRA 各通道压到 5 位（值域 0~31）拼一个 key，众数统计用。</summary>
    private static int Quantize(int bgra)
        => ((bgra >> 3) & 0x1F) | (((bgra >> 11) & 0x1F) << 5) | (((bgra >> 19) & 0x1F) << 10);
}
