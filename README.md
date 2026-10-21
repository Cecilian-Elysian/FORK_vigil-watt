# VigilWatt（个人 fork）

> [evren-oct/vigil-watt](https://github.com/evren-oct/vigil-watt) 的个人 fork（`Cecilian-Elysian/FORK_vigil-watt`）：只放自用改动，不发安装包，不碰上游；稳定版以上游 [Releases](https://github.com/evren-oct/vigil-watt/releases) 为准（[Gitee 镜像](https://gitee.com/YGWG-LX/vigil-watt/releases)）。
> 上游的完整介绍、界面截图与设备支持表见[上游 README](https://github.com/evren-oct/vigil-watt#readme)。项目文档在 `docs/`（入口 [docs/README.md](docs/README.md)）。

## 相对上游的修改

**一、任务栏电量贴片与其它任务栏工具的共存**（原理见 `docs/dev/runtime-pitfalls.md` §16.11）：

- **避让其他任务栏工具**：每拍枚举任务栏占位（**含顶层浮窗**，酷狗任务栏歌词就是浮窗），退到能放下的最靠右空隙；放不下逐档减显示台数，连 1 台都放不下则整体隐藏。
- **不叠 Win11 搜索框**：搜索框/任务按钮是 XAML 渲染、没有独立窗口，窗口枚举看不见它们（GDI 采样对任务栏表面实测全黑）——改用 UI Automation 读任务栏元素矩形，与枚举结果合并成占位。
- **贴片位置只跟窗口矩形走**：不跟窗口里的内容变化跑（酷狗悬停切控件态时贴片纹丝不动），扫描节拍 50ms。

## 构建与测试

需 .NET 8 SDK，详见 [docs/dev/build.md](docs/dev/build.md)：

```powershell
dotnet build VigilWatt.sln -c Debug
dotnet test src/tests/VigilWatt.Core.Tests --filter "FullyQualifiedName~Taskbar"
```

## 许可

[MIT License](LICENSE) © 2026 evren（上游）。本 fork 的改动同样以 MIT 提供。
