# 盖世小鸡 GameSir G7 Pro CE（dongle 3537）

> 从 [protocols.md](protocols.md) 拆出（2026-10-21）。结论均来自真机验证或官方软件抓包；改协议代码前先读本文。

## 1. 盖世小鸡 GameSir G7 Pro CE（dongle VID_3537）—— ✅ 已打通（心跳开流，不依赖官方软件）

### 1.1 接口与身份

dongle 枚举成「Xbox 360 Controller for Windows」+ 一个厂商 HID 集合 `usage_page 0xFFF0 / usage 0x0040`（in=64 / out=64）。这个集合就是官方 Connect 客户端连的那条配置通道（官方日志 `usagePage: 65520, interface: 1`）。

**PID 会变**：实测见过 `0x10B9`（官方设备表 13623:4281）和 `0x0575`。故插件**不按 PID 白名单硬卡**，只按 `VID_3537 + usage_page 0xFFF0` 认领——VID 已锁定厂商，误伤别家硬件风险为零。

> 注意：这是**手柄**，与 `3537:10B9` 的 XInput 呈现（`IG_00`）是同一物理设备的不同接口。XInput 的 `XInputGetBatteryInformation` 一律报 `batteryType=WIRED / FULL`，是假的，别用。

### 1.2 电量字段（官方 JS 逐行确认）

手柄状态流 report ID = `0x12`（官方 `Values.GamepadKeyStateReportId = 18`）：

```js
parseGamepadState(e){
  const chargeState = e[35];   // 充电状态
  const batteryLevel = e[36];  // 电量百分比
  ...
}
// 渲染层：isCharging: 1 == device.state.chargeState
```

- `[35] == 1` ⇒ 充电中；否则放电（100% 且未充电由 `ChargingStateMap` 收尾成"已充满"）。
- `[36]` 直接就是百分比，**不要做 bit7 拆包**——"电量 bit7 = 充电中"是官方另一条 `getDeviceInfo`（`SAPJOYUNI_DEVICEINFO`）路径的编码，套到状态流上会把 100% 之类的高值误读成"充电中 0~27%"。

**真机验证（2026-10-19）**：官方软件运行时抓 1431 帧，`[35]=0x00 / [36]=0x64`，与官方 vuex.json 缓存的 `chargeState:0 / batteryLevel:100` **完全一致**。

### 1.3 ✅ 心跳就是状态流的开关（已打通，不依赖官方软件）

真机实测（2026-10-19，官方 Connect 全程关闭）：

| 操作 | `0x12` 状态流 |
|---|---|
| 什么都不发 | **0 帧** |
| 发一帧心跳 `0F F2 00` | **497 帧 / 6 秒**（约 250 帧/秒） |
| 一帧心跳后按秒统计 | 第 1~2 秒各 250 帧，**第 3 秒起 0 帧** |
| 再发心跳 | 又恢复（315 帧 / 8 秒） |

结论：**dongle 只要收到心跳就开始推状态流，且只维持约 2 秒**。官方客户端每秒发一次心跳来维持它，
`destroy()` 时还会主动发 `Off`——这就是"官方一关就离线"的真正原因，不是解析问题。

心跳帧来自官方 JS 构造器（`bm(e=0) = [15, 242, e]`）：

```
0F F2 00 00 …（report 0x0F，cmd 0xF2，值 0x00，补零到接口声明的 64 字节）
```

**本插件的做法（突发模式）**：每轮轮询（60 秒）发一帧心跳，读 2 秒拿到电量就停，让流自然停止。
这与官方"每秒心跳 + 60 秒轮询电量"的行为同源但写入量更小（1 帧/分钟），且**不再需要官方软件**。

### 1.4 安全与开关

- `ProbeWrites => true`（探测也发心跳），写入受 `PluginEnabled["GameSir"]` + 基类熔断退避双重约束。
- **默认开启**：心跳只是保活、不改设备状态，风险低于北通的查询帧，故与北通一致。
- 离线判据：`CountPollFailures = true`——每轮确实发过帧，连续 3 次读不到状态帧才判离线（约 3 分钟熔断）。
  `Silence = 0`：没有常驻被动流，沉默不是判据。
- 与官方 Connect **可以同时开**（句柄可共享，实测互不干扰）。
