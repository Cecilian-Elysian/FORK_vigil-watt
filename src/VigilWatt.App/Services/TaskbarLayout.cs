namespace VigilWatt.App.Services;

/// <summary>屏幕坐标下的像素矩形（左上角 + 宽高）。任务栏定位全程物理像素，包一个类型免得四处传四个 int。</summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;
}

/// <summary>
/// 功 能： 任务栏贴片的定位数学：零 WinRT、零 P/Invoke 的纯函数，直接链进测试项目（R8）。
/// 2026/10/10 21:34:00 xliu
/// 窗口类只负责"把值喂进来、把结果写进 AppWindow"——算错位置这种事必须能在单测里复现。
/// </summary>
public static class TaskbarLayout
{
    /// <summary>贴片与任务栏两端的最小间距（物理像素）。</summary>
    public const int EdgeInsetPx = 4;

    /// <summary>任务栏收起后的判定厚度：自动隐藏后只剩约 2px 的一条边，贴片必须跟着一起藏。</summary>
    public const int HiddenThicknessPx = 4;

    /// <summary>任务栏当前是否不可见（自动隐藏已收起）。</summary>
    public static bool IsTaskbarHidden(PixelRect taskbar) =>
        Math.Min(taskbar.Width, taskbar.Height) <= HiddenThicknessPx;

    /// <summary>横向任务栏（贴片左右排）还是竖向（上下排）。</summary>
    public static bool IsHorizontal(PixelRect taskbar) => taskbar.Width >= taskbar.Height;

    /// <summary>贴片高度（物理像素）：按任务栏厚度减上下留白，再夹在 [minDip, maxDip] 内。</summary>
    public static int PillHeight(PixelRect taskbar, double scale, double marginDip, double minDip, double maxDip)
    {
        double thicknessDip = (IsHorizontal(taskbar) ? taskbar.Height : taskbar.Width) / scale;
        return (int)Math.Ceiling(Math.Clamp(thicknessDip - marginDip * 2, minDip, maxDip) * scale);
    }

    /// <summary>贴片可用的轴向区间：横放是任务栏左缘到托盘左缘，竖放是上缘到托盘上缘。</summary>
    public static (int Min, int Max) Band(PixelRect taskbar, PixelRect tray, bool horizontal) => horizontal
        ? (taskbar.Left + EdgeInsetPx, tray.Width > 0 ? tray.Left : taskbar.Right)
        : (taskbar.Top + EdgeInsetPx, tray.Height > 0 ? tray.Top : taskbar.Bottom);

    /// <summary>
    /// 贴片左上角（物理像素）。默认位 = 紧贴托盘左边（竖向任务栏则是托盘上边）；该位置被别的
    /// 任务栏工具占了就退到能放下的最靠右空隙，再叠加用户拖动出来的 <paramref name="offset"/>。
    /// 偏移相对"实际锚点"记账，锚点被占而移动时用户拖出来的相对位置不丢。
    /// </summary>
    public static Placement PlaceAvoiding(
        PixelRect taskbar, PixelRect tray, int pillW, int pillH, int offset, int gapPx,
        IReadOnlyList<PixelRect> occupied)
    {
        bool horizontal = IsHorizontal(taskbar);
        var spans = ProjectSpans(occupied, taskbar, horizontal);

        if (horizontal)
        {
            var (bandMin, bandMax) = Band(taskbar, tray, true);
            bool fits = TryPlaceAlong(bandMin, bandMax, pillW, gapPx, spans, out int anchor, out _);
            if (!fits) anchor = bandMax - pillW - gapPx;
            int min = taskbar.Left + EdgeInsetPx;
            int max = Math.Max(min, taskbar.Right - pillW - EdgeInsetPx);
            int x = Math.Clamp(anchor + offset, min, max);
            int y = taskbar.Top + (taskbar.Height - pillH) / 2;
            return new Placement(x, y, x - anchor, fits);
        }

        var (bandMinV, bandMaxV) = Band(taskbar, tray, false);
        bool fitsV = TryPlaceAlong(bandMinV, bandMaxV, pillH, gapPx, spans, out int anchorY, out _);
        if (!fitsV) anchorY = bandMaxV - pillH - gapPx;
        int minY = taskbar.Top + EdgeInsetPx;
        int maxY = Math.Max(minY, taskbar.Bottom - pillH - EdgeInsetPx);
        int yPos = Math.Clamp(anchorY + offset, minY, maxY);

        int centeredX = taskbar.Left + (taskbar.Width - pillW) / 2;
        if (pillW > taskbar.Width)
        {
            // 竖向任务栏只有 40~50px 宽、装不下横向胶囊：居中会让它一半跑到屏幕外，
            // 改挂在"桌面那一侧"——左停靠往右伸、右停靠往左伸
            centeredX = taskbar.Left <= 0 ? taskbar.Left : taskbar.Right - pillW;
        }
        return new Placement(centeredX, yPos, yPos - anchorY, fitsV);
    }

    /// <summary>贴片落点：屏幕坐标 + 相对锚点的偏移 + 这次是否放得下。</summary>
    public readonly record struct Placement(int X, int Y, int Offset, bool Fits);

    /// <summary>轴上的一段占用区间（左闭右开按端点算，空区间忽略）。</summary>
    public readonly record struct Span(int Left, int Right);

    /// <summary>
    /// 把占位矩形投影到任务栏主轴：横向任务栏取 X 跨度，竖向取 Y 跨度。
    /// 不与任务栏条带相交的矩形不算占位（浮在桌面上的窗口不该影响任务栏）。
    /// </summary>
    public static List<Span> ProjectSpans(IReadOnlyList<PixelRect> occupied, PixelRect taskbar, bool horizontal)
    {
        var list = new List<Span>();
        foreach (var r in occupied)
        {
            if (horizontal)
            {
                if (r.Top >= taskbar.Bottom || r.Bottom <= taskbar.Top) continue;
                list.Add(new Span(r.Left, r.Right));
            }
            else
            {
                if (r.Left >= taskbar.Right || r.Right <= taskbar.Left) continue;
                list.Add(new Span(r.Top, r.Bottom));
            }
        }
        return list;
    }

    /// <summary>区间合并后求差集：返回 [bandMin, bandMax] 里没被占的空隙（从左到右）。</summary>
    public static List<Span> FreeGaps(int bandMin, int bandMax, IReadOnlyList<Span> occupied)
    {
        var sorted = occupied.Where(s => s.Right > bandMin && s.Left < bandMax).OrderBy(s => s.Left).ToList();
        var gaps = new List<Span>();
        int cursor = bandMin;
        foreach (var s in sorted)
        {
            int left = Math.Max(s.Left, bandMin);
            int right = Math.Min(s.Right, bandMax);
            if (right <= cursor) continue;
            if (left > cursor) gaps.Add(new Span(cursor, left));
            cursor = right;
        }
        if (cursor < bandMax) gaps.Add(new Span(cursor, bandMax));
        return gaps;
    }

    /// <summary>
    /// 在轴上落位：默认位（贴 bandMax 端）空闲就用它；被占则退到能放下的最靠右空隙。
    /// 空隙窄到放不下时返回 false，由调用方决定收窄内容还是隐藏。
    /// <paramref name="slack"/> 是贴片左边还剩多少空隙（往宽了长会往左长，这就是能长的余量）。
    /// </summary>
    public static bool TryPlaceAlong(
        int bandMin, int bandMax, int length, int gapPx, IReadOnlyList<Span> occupied,
        out int pos, out int slack)
    {
        pos = 0;
        slack = 0;
        if (bandMax - bandMin < length) return false;

        var gaps = FreeGaps(bandMin, bandMax, occupied);

        int def = bandMax - length - gapPx;
        if (def >= bandMin && !Overlaps(def, def + length, occupied))
        {
            pos = def;
            slack = SlackToLeft(gaps, def);
            return true;
        }

        for (int i = gaps.Count - 1; i >= 0; i--)
        {
            int x = gaps[i].Right - length - gapPx;
            if (x < gaps[i].Left) x = gaps[i].Left;
            if (x + length > gaps[i].Right) continue;
            pos = x;
            slack = x - gaps[i].Left;
            return true;
        }
        return false;
    }

    /// <summary>贴片左边缘到所在空隙左边缘的距离：往宽了长就往左长，这是还剩多少余量。</summary>
    private static int SlackToLeft(List<Span> gaps, int x)
    {
        foreach (var g in gaps)
        {
            if (x >= g.Left && x < g.Right) return x - g.Left;
        }
        return 0;
    }

    /// <summary>区间 [a0, a1) 与任一占位区间相交。</summary>
    public static bool Overlaps(int a0, int a1, IReadOnlyList<Span> occupied)
    {
        foreach (var s in occupied)
        {
            if (a0 < s.Right && a1 > s.Left) return true;
        }
        return false;
    }

    /// <summary>某个台数放不放得下，以及放下后左边还剩多少余量（往宽了长就往左长）。</summary>
    public readonly record struct Fit(bool Fits, int Slack);

    /// <summary>
    /// 选显示几台设备：现状放得下就保持（否则每秒重算会抖），放不下逐档收窄到 1 台，
    /// 宽裕时逐档放宽。返回 0 表示连 1 台都放不下，调用方应当隐藏贴片。
    /// 放宽要留出 <paramref name="widenMarginPx"/> 余量，否则宽度卡在临界值时台数会来回横跳。
    /// </summary>
    public static int NextDeviceCount(int current, int userMax, IReadOnlyList<Fit> fits, int widenMarginPx)
    {
        if (fits.Count < 2) return 0;
        int max = Math.Clamp(userMax, 1, fits.Count - 1);
        if (current < 1) current = 1;
        if (current > max) current = max;

        if (!fits[current].Fits)
        {
            for (int k = current - 1; k >= 1; k--)
            {
                if (fits[k].Fits) return k;
            }
            return 0;
        }
        if (current < max && fits[current + 1].Fits && fits[current + 1].Slack >= widenMarginPx)
        {
            return current + 1;
        }
        return current;
    }

    /// <summary>把记下偏移时的物理像素换算到当前 DPI；Dpi=0 或未变则原样返回（勿逐帧累乘，见 §16.5）。</summary>
    public static int ScaleOffset(int offsetPx, uint storedDpi, uint currentDpi)
    {
        if (storedDpi == 0 || currentDpi == 0 || storedDpi == currentDpi) return offsetPx;
        return (int)Math.Round(offsetPx * (double)currentDpi / storedDpi);
    }

    /// <summary>客户区 X 落在哪一格；分隔缝返回 -1。</summary>
    public static int HitTest(IReadOnlyList<(int X, int Width)> regions, int clientX)
    {
        for (int i = 0; i < regions.Count; i++)
        {
            var r = regions[i];
            if (clientX >= r.X && clientX < r.X + r.Width) return i;
        }
        return -1;
    }

    /// <summary>
    /// 提示框左上角（屏幕坐标）：摆在任务栏朝桌面一侧，水平/竖直对准格子中心，再夹进工作区。
    /// </summary>
    public static (int X, int Y) PlaceTooltip(
        PixelRect taskbar, PixelRect chipScreen, int tipW, int tipH, int gapPx, PixelRect workArea)
    {
        int x, y;
        if (IsHorizontal(taskbar))
        {
            x = chipScreen.Left + (chipScreen.Width - tipW) / 2;
            // Top==0 视为顶栏，否则底栏（主屏底栏 Top 很大；副屏同理）
            y = taskbar.Top <= 0
                ? chipScreen.Bottom + gapPx
                : chipScreen.Top - tipH - gapPx;
        }
        else
        {
            y = chipScreen.Top + (chipScreen.Height - tipH) / 2;
            // Left==0 左停靠 → 提示往右伸到桌面；否则右停靠往左
            x = taskbar.Left <= 0
                ? chipScreen.Right + gapPx
                : chipScreen.Left - tipW - gapPx;
        }

        if (workArea.Width > 0 && workArea.Height > 0)
        {
            x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - tipW));
            y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - tipH));
        }
        return (x, y);
    }
}
