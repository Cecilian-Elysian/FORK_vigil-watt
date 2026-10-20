using System.Windows.Automation;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 用 UIA 读 Win11 任务栏 XAML 元素（搜索框/任务按钮/托盘）的矩形——这些 UI 无独立子窗口、GDI 采样全黑，UIA 是唯一看得见的路。
/// 2026/10/21 00:02:59 lym18
/// 后台 500ms 一拍刷新，UI 线程只读不可变快照；匹配按类名/AutomationId，Name 随系统语言变不可靠。
/// </summary>
internal static class TaskbarXamlRects
{
    /// <summary>后台刷新节拍。系统 UI 位置变化极低频（开关应用才动），对 explorer 的跨进程请求频率压到最低。</summary>
    private const int RefreshIntervalMs = 500;

    /// <summary>AutomationId 精确命中的系统按钮；其余靠类名模式兜（Taskbar.*、SystemTray.*、*AutomationPeer）。</summary>
    private static readonly HashSet<string> KnownIds = new(StringComparer.Ordinal)
    {
        "StartButton", "SearchButton", "SearchGleamButton", "TaskViewButton",
    };

    private static Snapshot _snap = Snapshot.Empty;
    private static System.Threading.Timer? _timer;

    /// <summary>explorer 忙时 Refresh 可能超时节拍，Timer 会重入——防重入只是省掉无谓的并发采集。</summary>
    private static int _refreshing;

    /// <summary>最近一拍收到的系统元素矩形（屏幕坐标）。UIA 失败（锁屏/安全桌面）时保持上次结果。</summary>
    public static IReadOnlyList<PixelRect> Cached => _snap.Rects;

    /// <summary>托盘（TrayNotifyWnd）矩形：26200 起它不再是 Win32 子窗口，锚点只能从这里拿。</summary>
    public static bool TryGetTray(out PixelRect tray)
    {
        tray = _snap.Tray;
        return tray.Width > 0 && tray.Height > 0;
    }

    /// <summary>
    /// 由主窗口启动时调一次。UIA 是跨进程 COM，调用可能慢甚至卡住，绝不能碰 UI 线程（CR-63 教训）；
    /// Timer 引用必须压住，否则 GC 回收后节拍就停了。
    /// </summary>
    public static void Start()
    {
        _timer ??= new System.Threading.Timer(_ => Refresh(), null, 0, RefreshIntervalMs);
    }

    private static void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            var hwnd = Native.FindWindowW(Native.TaskbarClassName, null);
            if (hwnd == IntPtr.Zero) return;
            var root = AutomationElement.FromHandle(hwnd);
            var rootRect = root.Current.BoundingRectangle;
            if (rootRect.IsEmpty) return;

            var rects = new List<PixelRect>();
            PixelRect tray = default;
            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                var c = e.Current;
                var r = c.BoundingRectangle;
                if (r.IsEmpty || !IsSystemUi(c.AutomationId, c.ClassName)) continue;
                var rect = new PixelRect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
                // Taskbar.TaskbarFrame / InputSite 是整条容器：宽过任务栏的一律不是占位，
                // 漏掉这条过滤整条任务栏都会被当障碍、贴片无处可放
                if (rect.Width >= (int)rootRect.Width) continue;
                if (c.ClassName == "TrayNotifyWnd") tray = rect;
                else rects.Add(rect);
            }

            _snap = new Snapshot(rects, tray);
        }
        catch
        {
            // 锁屏、安全桌面、explorer 忙：保持上次快照，贴片按旧占位继续工作
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private static bool IsSystemUi(string automationId, string className) =>
        KnownIds.Contains(automationId)
        || className == "TrayNotifyWnd"
        || className.StartsWith("Taskbar.", StringComparison.Ordinal)
        || className.StartsWith("SystemTray.", StringComparison.Ordinal)
        || className.EndsWith("AutomationPeer", StringComparison.Ordinal);

    /// <summary>不可变快照：整包引用替换，读侧永远拿到自洽的一帧，不用加锁。</summary>
    private sealed class Snapshot(List<PixelRect> rects, PixelRect tray)
    {
        public static readonly Snapshot Empty = new([], default);

        public List<PixelRect> Rects { get; } = rects;
        public PixelRect Tray { get; } = tray;
    }
}
