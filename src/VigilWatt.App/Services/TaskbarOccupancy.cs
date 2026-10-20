using System.Runtime.InteropServices;
using System.Text;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 枚举任务栏条带上被别的工具占住的矩形，供贴片避让用。
/// 2026/10/20 15:20:00 xliu
/// 第三方工具不一定挂在任务栏下（酷狗任务栏歌词是顶层浮窗），所以任务栏子窗口与顶层浮窗都要扫。
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
    /// 收集占位矩形（屏幕坐标）。任务栏子窗口 + 压在任务栏条带上的顶层浮窗都算；
    /// 自己的窗口、不可见窗口、DWM 隐藏的壳一律排除。
    /// <paramref name="fullWindows"/> 可选，收**未收窄**的窗口矩形：内容感知只对落位负责，
    /// "贴片不许盖住光标"那类判断要的是对方完整窗口（酷狗控件态收窄到 1451，光标停在 1400 也算在它上面）。
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

    /// <summary>
    /// 第三方窗（酷狗会把歌词窗嵌成任务栏子窗口，顶层/子窗口两条路都得采样）按内容左缘收窄占位。
    /// explorer 自家窗口不采：任务按钮之间有透明缝，收窄了贴片会钻进按钮空当。
    /// </summary>
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

    /// <summary>
    /// 浮窗在条带里画了内容的最左一列屏幕 x；全空/失败返回 null（调用方回退窗口矩形）。
    /// 横向条带专用（竖放贴片不走这条路径）；区域上下各收 6px 避开窗框边。
    /// <paramref name="selfRect"/> 是贴片自己的矩形：贴片贴进浮窗空白区后，它的亮色像素会被
    /// BitBlt 一起采回来，内容左缘误判到贴片边上——占位变宽贴片退、退出去又贴回来，来回闪。
    /// </summary>
    private static int? LeftmostContentX(PixelRect windowRect, PixelRect taskbar, PixelRect? selfRect)
    {
        int top = Math.Max(windowRect.Top, taskbar.Top) + 6;
        int height = Math.Min(windowRect.Bottom, taskbar.Bottom) - 6 - top;
        int width = windowRect.Width;
        if (width <= 0 || height <= 0) return null;

        // 吞掉一切异常：采样是尽力而为的增强，失败只能回退窗口矩形，绝不能打断放贴片的主流程
        try
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            try
            {
                var bmi = new Native.BITMAPINFO();
                bmi.bmiHeader.biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>();
                bmi.bmiHeader.biWidth = width;
                bmi.bmiHeader.biHeight = -height; // 负数 = top-down，行序与 y 一致
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;

                IntPtr memDc = Native.CreateCompatibleDC(screen);
                if (memDc == IntPtr.Zero) return null;
                try
                {
                    IntPtr bits;
                    IntPtr dib = Native.CreateDIBSection(memDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
                    if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
                    try
                    {
                        IntPtr old = Native.SelectObject(memDc, dib);
                        bool ok = Native.BitBlt(memDc, 0, 0, width, height, screen, windowRect.Left, top, Native.SRCCOPY);
                        Native.SelectObject(memDc, old);
                        if (!ok) return null;
                        int? col = ScanLeftmost(bits, width, height, windowRect.Left, selfRect);
                        return col is int c ? windowRect.Left + c : null;
                    }
                    finally { Native.DeleteObject(dib); }
                }
                finally { Native.DeleteDC(memDc); }
            }
            finally { Native.ReleaseDC(IntPtr.Zero, screen); }
        }
        catch { return null; }
    }

    /// <summary>逐列扫第一个"与背景差 >20/通道"的像素列；背景色取区域众数，细笔画文字赢不了实心背景。</summary>
    private static int? ScanLeftmost(IntPtr bits, int width, int height, int regionLeft, PixelRect? selfRect)
    {
        var counts = new Dictionary<int, int>();
        for (int i = 0; i < width * height; i++)
        {
            int key = Quantize(Marshal.ReadInt32(bits, i * 4));
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        int best = 0, bestCount = -1;
        foreach (var kv in counts)
        {
            if (kv.Value > bestCount) { bestCount = kv.Value; best = kv.Key; }
        }
        int bgB = (best & 0x1F) << 3, bgG = ((best >> 5) & 0x1F) << 3, bgR = ((best >> 10) & 0x1F) << 3;

        for (int x = 0; x < width; x++)
        {
            // 贴片自己盖住的列跳过：那里读到的是贴片不是浮窗内容
            int screenX = regionLeft + x;
            if (selfRect is PixelRect s && screenX >= s.Left && screenX < s.Right) continue;
            for (int y = 0; y < height; y++)
            {
                int c = Marshal.ReadInt32(bits, (y * width + x) * 4);
                int dB = (c & 0xFF) - bgB, dG = ((c >> 8) & 0xFF) - bgG, dR = ((c >> 16) & 0xFF) - bgR;
                if (Math.Abs(dB) > 20 || Math.Abs(dG) > 20 || Math.Abs(dR) > 20) return x;
            }
        }
        return null;
    }

    /// <summary>BGRA 各通道压到 5 位（值域 0~31）拼一个 key，众数统计用。</summary>
    private static int Quantize(int bgra)
        => ((bgra >> 3) & 0x1F) | (((bgra >> 11) & 0x1F) << 5) | (((bgra >> 19) & 0x1F) << 10);
}
