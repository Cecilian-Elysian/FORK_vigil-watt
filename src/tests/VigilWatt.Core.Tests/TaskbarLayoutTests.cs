using VigilWatt.App.Services;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 任务栏贴片的定位数学（T1–T9）。
/// 2026/10/10 21:34:00 xliu
/// 这块算错的表现是"贴片跑到屏幕外 / 盖住时钟 / 拖动空走一截"， 都是纯几何，必须能在单测里复现。
/// </summary>
public class TaskbarLayoutTests
{
    // 底部任务栏：整条 1920 宽、48 厚；通知区域（托盘）占最右 220
    private static readonly PixelRect BottomTaskbar = new(0, 1032, 1920, 48);
    private static readonly PixelRect BottomTray = new(1700, 1032, 220, 48);

    // T1：自动隐藏收起时任务栏只剩约 2px 厚的一条边 —— 必须判成"不可见"
    [Theory]
    [InlineData(1920, 48, false)]
    [InlineData(1920, 2, true)]
    [InlineData(2, 1080, true)]
    public void IsTaskbarHidden_ByThickness(int width, int height, bool hidden)
        => Assert.Equal(hidden, TaskbarLayout.IsTaskbarHidden(new PixelRect(0, 0, width, height)));

    // T2：横竖判定按"长边"来（宽 ≥ 高即横向）
    [Fact]
    public void IsHorizontal_ByLongerSide()
    {
        Assert.True(TaskbarLayout.IsHorizontal(BottomTaskbar));
        Assert.False(TaskbarLayout.IsHorizontal(new PixelRect(0, 0, 48, 1080)));
    }

    // T3：贴片高度 = 任务栏厚度 - 上下留白，并夹在 [min, max] 内（返回值是物理像素）
    [Fact]
    public void PillHeight_SubtractsMarginAndClamps()
    {
        // 100% 缩放：48 - 6*2 = 36
        Assert.Equal(36, TaskbarLayout.PillHeight(BottomTaskbar, 1.0, 6, 22, 40));
        // 150% 缩放：任务栏 72 物理像素 → 48 DIP → 36 DIP → 54 物理像素
        Assert.Equal(54, TaskbarLayout.PillHeight(new PixelRect(0, 1008, 1920, 72), 1.5, 6, 22, 40));
        // 很薄的任务栏（Win10 40px）：40 - 12 = 28，仍在范围内
        Assert.Equal(28, TaskbarLayout.PillHeight(new PixelRect(0, 1040, 1920, 40), 1.0, 6, 22, 40));
        // 极薄：30 - 12 = 18 → 夹到下限 22
        Assert.Equal(22, TaskbarLayout.PillHeight(new PixelRect(0, 1050, 1920, 30), 1.0, 6, 22, 40));
        // 极厚：120 - 12 = 108 → 夹到上限 40
        Assert.Equal(40, TaskbarLayout.PillHeight(new PixelRect(0, 960, 1920, 120), 1.0, 6, 22, 40));
    }

    // T4：默认位 = 紧贴托盘左边，垂直居中；未拖动时 offset 反推回 0
    [Fact]
    public void Place_DefaultsToLeftOfTray()
    {
        var p = TaskbarLayout.PlaceAvoiding(BottomTaskbar, BottomTray, 200, 36, 0, 6, NoOccupancy);

        Assert.Equal(1700 - 200 - 6, p.X);          // 1494
        Assert.Equal(1032 + (48 - 36) / 2, p.Y);    // 1038
        Assert.Equal(0, p.Offset);
        Assert.True(p.Fits);
    }

    // T5：托盘找不到时退回任务栏右缘，不能因此不显示
    [Fact]
    public void Place_FallsBackToTaskbarRightEdge()
    {
        var p = TaskbarLayout.PlaceAvoiding(BottomTaskbar, default, 200, 36, 0, 6, NoOccupancy);

        Assert.Equal(1920 - 200 - 6, p.X);
    }

    // T6：向右拖到头会被夹住，并把"实际落点"反推回 offset —— 这样往回拖不会先空走一截
    [Fact]
    public void Place_ClampsAtRightAndBackDerivesOffset()
    {
        var p = TaskbarLayout.PlaceAvoiding(BottomTaskbar, BottomTray, 200, 36, 5000, 6, NoOccupancy);

        Assert.Equal(1920 - 200 - TaskbarLayout.EdgeInsetPx, p.X);
        Assert.Equal(p.X - (1700 - 200 - 6), p.Offset);
    }

    // T7：向左拖到头同理
    [Fact]
    public void Place_ClampsAtLeftAndBackDerivesOffset()
    {
        var p = TaskbarLayout.PlaceAvoiding(BottomTaskbar, BottomTray, 200, 36, -5000, 6, NoOccupancy);

        Assert.Equal(TaskbarLayout.EdgeInsetPx, p.X);
        Assert.Equal(p.X - (1700 - 200 - 6), p.Offset);
    }

    // T8：竖向（左停靠）任务栏：贴片挂在托盘上边，胶囊比任务栏宽时往桌面一侧伸、不出屏
    [Fact]
    public void Place_VerticalTaskbar_AnchorsAboveTray()
    {
        var taskbar = new PixelRect(0, 0, 48, 1080);
        var tray = new PixelRect(0, 860, 48, 220);

        var p = TaskbarLayout.PlaceAvoiding(taskbar, tray, 200, 36, 0, 6, NoOccupancy);

        Assert.Equal(0, p.X);                     // 左停靠 → 从任务栏左缘往右伸，不会跑到屏幕外
        Assert.Equal(860 - 36 - 6, p.Y);
    }

    // T9：竖向（右停靠）任务栏：胶囊往左伸，右缘对齐任务栏右缘
    [Fact]
    public void Place_VerticalRightDockedTaskbar_HangsLeftward()
    {
        var taskbar = new PixelRect(1872, 0, 48, 1080);

        var p = TaskbarLayout.PlaceAvoiding(taskbar, default, 200, 36, 0, 6, NoOccupancy);

        Assert.Equal(1872 + 48 - 200, p.X);
    }

    // T15：命中落在格内；分隔缝（格与格之间）返回 -1
    [Fact]
    public void HitTest_ChipVersusGap()
    {
        (int X, int Width)[] regions = [(6, 40), (56, 40)];
        Assert.Equal(0, TaskbarLayout.HitTest(regions, 6));
        Assert.Equal(0, TaskbarLayout.HitTest(regions, 45));
        Assert.Equal(-1, TaskbarLayout.HitTest(regions, 50)); // 缝
        Assert.Equal(1, TaskbarLayout.HitTest(regions, 56));
        Assert.Equal(-1, TaskbarLayout.HitTest(regions, 100));
    }

    // T16：底栏提示在格子上方；顶栏在下方；靠边时夹进工作区
    [Fact]
    public void PlaceTooltip_OutsideTaskbarAndClamps()
    {
        var work = new PixelRect(0, 0, 1920, 1080);
        var chip = new PixelRect(1494, 1038, 40, 36);

        var (bx, by) = TaskbarLayout.PlaceTooltip(BottomTaskbar, chip, 120, 28, 4, work);
        Assert.Equal(1494 + (40 - 120) / 2, bx);
        Assert.Equal(1038 - 28 - 4, by);

        var topBar = new PixelRect(0, 0, 1920, 48);
        var topChip = new PixelRect(1494, 6, 40, 36);
        var (_, ty) = TaskbarLayout.PlaceTooltip(topBar, topChip, 120, 28, 4, work);
        Assert.Equal(6 + 36 + 4, ty);

        // 格子贴左缘：提示左缘夹到工作区左缘
        var edgeChip = new PixelRect(2, 1038, 40, 36);
        var (ex, _) = TaskbarLayout.PlaceTooltip(BottomTaskbar, edgeChip, 200, 28, 4, work);
        Assert.Equal(0, ex);
    }

    // T17：左停靠提示往右；右停靠往左
    [Fact]
    public void PlaceTooltip_VerticalTaskbar_TowardDesktop()
    {
        var work = new PixelRect(0, 0, 1920, 1080);
        var leftBar = new PixelRect(0, 0, 48, 1080);
        var leftChip = new PixelRect(0, 400, 200, 36);
        var (lx, _) = TaskbarLayout.PlaceTooltip(leftBar, leftChip, 120, 28, 4, work);
        Assert.Equal(200 + 4, lx);

        var rightBar = new PixelRect(1872, 0, 48, 1080);
        var rightChip = new PixelRect(1720, 400, 200, 36);
        var (rx, _) = TaskbarLayout.PlaceTooltip(rightBar, rightChip, 120, 28, 4, work);
        Assert.Equal(1720 - 120 - 4, rx);
    }

    // T18：占位区间先合并再求差集；越界的部分要裁掉
    [Fact]
    public void FreeGaps_MergesAndClips()
    {
        var gaps = TaskbarLayout.FreeGaps(0, 100, new[]
        {
            new TaskbarLayout.Span(10, 30),
            new TaskbarLayout.Span(20, 40),   // 与上一段相交，要并成一段
            new TaskbarLayout.Span(60, 70),
        });

        Assert.Equal(3, gaps.Count);
        Assert.Equal(new TaskbarLayout.Span(0, 10), gaps[0]);
        Assert.Equal(new TaskbarLayout.Span(40, 60), gaps[1]);
        Assert.Equal(new TaskbarLayout.Span(70, 100), gaps[2]);
    }

    // T19：默认位空闲就用默认位；被占则退到最靠右的空隙；都放不下返回 false
    [Fact]
    public void TryPlaceAlong_DefaultThenRightmostGap()
    {
        var none = Array.Empty<TaskbarLayout.Span>();

        Assert.True(TaskbarLayout.TryPlaceAlong(0, 100, 20, 5, none, out int free, out int slack));
        Assert.Equal(100 - 20 - 5, free);
        Assert.Equal(75, slack);              // 左边还能长 75px

        // 挡住默认位：退到它左边最靠右的空隙
        Assert.True(TaskbarLayout.TryPlaceAlong(0, 100, 20, 5, new[] { new TaskbarLayout.Span(70, 100) }, out int left, out int leftSlack));
        Assert.Equal(70 - 20 - 5, left);
        Assert.Equal(45, leftSlack);

        // 整条被占：放不下
        Assert.False(TaskbarLayout.TryPlaceAlong(0, 100, 20, 5, new[] { new TaskbarLayout.Span(0, 100) }, out _, out _));
    }

    // T20：酷狗任务栏歌词这类浮窗占住贴托盘的位置时，贴片退到它左边，不叠字
    [Fact]
    public void PlaceAvoiding_FloatingWindowOnTray_SlidesLeft()
    {
        // 本机实测：托盘左缘 1573，歌词窗 1373~1573，任务按钮 55~587
        var tray = new PixelRect(1573, 1032, 347, 48);
        var lyric = new PixelRect(1373, 1032, 200, 48);
        var taskButtons = new PixelRect(55, 1032, 532, 48);

        var p = TaskbarLayout.PlaceAvoiding(
            BottomTaskbar, tray, 97, 36, 0, 6, new[] { lyric, taskButtons });

        Assert.Equal(1373 - 97 - 6, p.X);   // 1270：贴着歌词窗左边 6px
        Assert.True(p.Fits);
        Assert.Equal(0, p.Offset);          // 偏移相对新锚点记账
    }

    // T21：用户拖出来的相对位置要跟着新锚点走，不能因为避让就丢掉
    [Fact]
    public void PlaceAvoiding_OffsetIsRelativeToMovedAnchor()
    {
        var tray = new PixelRect(1573, 1032, 347, 48);
        var lyric = new PixelRect(1373, 1032, 200, 48);

        var p = TaskbarLayout.PlaceAvoiding(BottomTaskbar, tray, 97, 36, 50, 6, new[] { lyric });

        Assert.Equal(1373 - 97 - 6 + 50, p.X);
    }

    // T22：台数状态机——放得下就不动，放不下逐档收窄，宽裕时逐档放宽，一台都放不下返回 0
    [Fact]
    public void NextDeviceCount_KeepsNarrowsWidens()
    {
        TaskbarLayout.Fit[] threeOk = [default, new(true, 100), new(true, 100), new(true, 100)];
        Assert.Equal(3, TaskbarLayout.NextDeviceCount(3, 3, threeOk, 10));      // 现状不动
        Assert.Equal(3, TaskbarLayout.NextDeviceCount(2, 3, threeOk, 10));      // 还能放宽

        TaskbarLayout.Fit[] onlyTwo = [default, new(true, 100), new(true, 100), new(false, 0)];
        Assert.Equal(2, TaskbarLayout.NextDeviceCount(3, 3, onlyTwo, 10));      // 收窄一档
        Assert.Equal(2, TaskbarLayout.NextDeviceCount(1, 3, onlyTwo, 10));      // 放宽一档

        TaskbarLayout.Fit[] onlyOne = [default, new(true, 100), new(false, 0), new(false, 0)];
        Assert.Equal(1, TaskbarLayout.NextDeviceCount(3, 3, onlyOne, 10));      // 收到 1 台
        Assert.Equal(1, TaskbarLayout.NextDeviceCount(1, 3, onlyOne, 10));      // 只有 1 台放得下

        TaskbarLayout.Fit[] none = [default, new(false, 0), new(false, 0), new(false, 0)];
        Assert.Equal(0, TaskbarLayout.NextDeviceCount(1, 3, none, 10));         // 一台都放不下
    }

    // T23：放宽要留余量——宽度卡在临界值时台数不能来回横跳（10Hz 扫描下会闪）
    [Fact]
    public void NextDeviceCount_WidenNeedsMargin()
    {
        // 第二台放得下但只剩 3px 余量：不够 margin，不放宽
        TaskbarLayout.Fit[] tight = [default, new(true, 50), new(true, 3), new(false, 0)];
        Assert.Equal(1, TaskbarLayout.NextDeviceCount(1, 3, tight, 10));

        // 余量够了：放宽
        TaskbarLayout.Fit[] roomy = [default, new(true, 50), new(true, 30), new(false, 0)];
        Assert.Equal(2, TaskbarLayout.NextDeviceCount(1, 3, roomy, 10));
    }

    private static readonly PixelRect[] NoOccupancy = Array.Empty<PixelRect>();
}

/// <summary>任务栏贴片的两个设置项收敛（T10–T12）：手改坏了的 settings.json 不能把界面带崩。</summary>
public class TaskbarBatteryOptionsTests
{
    // T10：显示内容只认两个合法值，其余（含 null / 手改的乱值）一律按 All
    [Theory]
    [InlineData(null, TaskbarBatteryMode.All)]
    [InlineData("", TaskbarBatteryMode.All)]
    [InlineData("Lowest", TaskbarBatteryMode.Lowest)]
    [InlineData("All", TaskbarBatteryMode.All)]
    [InlineData("lowest", TaskbarBatteryMode.All)]
    [InlineData("乱写的", TaskbarBatteryMode.All)]
    public void ModeNormalize_OnlyAcceptsKnownValues(string? input, string expected)
        => Assert.Equal(expected, TaskbarBatteryMode.Normalize(input));

    // T11：最多台数收敛到 1~5；0（老配置缺字段）按默认 3
    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(99, 5)]
    [InlineData(-4, 1)]
    public void MaxDevicesClamp_IntoRange(int input, int expected)
        => Assert.Equal(expected, TaskbarBatteryOptions.Clamp(input));

    // T12：下拉框档位必须与 Clamp 的取值范围一致（防止两处常量漂移）
    [Fact]
    public void Choices_MatchClampRange()
    {
        Assert.Equal(TaskbarBatteryOptions.Max - TaskbarBatteryOptions.Min + 1, TaskbarBatteryOptions.Choices.Count);
        Assert.All(TaskbarBatteryOptions.Choices,
            n => Assert.Equal(n, TaskbarBatteryOptions.Clamp(n)));
    }
}
