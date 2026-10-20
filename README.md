# VigilWatt（个人 fork）

> [evren-oct/vigil-watt](https://github.com/evren-oct/vigil-watt) 的个人 fork（`Cecilian-Elysian/FORK_vigil-watt`）：只放自用改动，不发安装包，不碰上游；稳定版以上游 [Releases](https://github.com/evren-oct/vigil-watt/releases) 为准（[Gitee 镜像](https://gitee.com/YGWG-LX/vigil-watt/releases)）。
> 上游的完整介绍、界面截图与设备支持表见[上游 README](https://github.com/evren-oct/vigil-watt#readme)。项目文档在 `docs/`（入口 [docs/README.md](docs/README.md)）。

## 相对上游的修改

全部集中在**任务栏电量贴片与其它任务栏工具的共存**上（原理见 `docs/dev/runtime-pitfalls.md` §16.11）：

- **避让其他任务栏工具**（`1f94752`）：每拍枚举任务栏占位（**含顶层浮窗**，酷狗任务栏歌词就是浮窗），退到能放下的最靠右空隙；放不下逐档减显示台数，连 1 台都放不下则整体隐藏。不收窄任务列表——两个工具同时收窄会来回跳。
- **跟着邻窗变形走**（`0106d2f`）：占位扫描提到 100ms 一拍（实测 ~60ms 跟上），位图没变不重贴，台数放宽留一个 gap 余量防横跳。
- **内容感知避让**（`69b966b`）：酷狗歌词窗矩形恒为 200px、悬停只换内容，按窗口矩形避让在控件态会隔 ~80px 空白。现对第三方占位窗 BitBlt 采样屏幕像素（众数色当背景、逐列找首个差异列），用内容真实左缘收窄占位——控件态贴片精确贴住图标。
- **修两处来回闪**（同上）：采样扫描跳过贴片自身矩形覆盖的列（防自我污染）；光标悬在占位窗上时每拍让出该窗完整矩形（防"贴住光标→对方失悬停→贴了又退"死循环）。

## 构建与测试

需 .NET 8 SDK，详见 [docs/dev/build.md](docs/dev/build.md)：

```powershell
dotnet build VigilWatt.sln -c Debug
dotnet test src/tests/VigilWatt.Core.Tests --filter "FullyQualifiedName~Taskbar"
```

## 许可

[MIT License](LICENSE) © 2026 evren（上游）。本 fork 的改动同样以 MIT 提供。
