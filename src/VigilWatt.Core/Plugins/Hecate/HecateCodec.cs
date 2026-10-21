using VigilWatt.Core.Models;

namespace VigilWatt.Core.Plugins.Hecate;

internal enum HecateFrameKind
{
    NotStatus,
    Status,
    StatusInvalidPercent,
}

internal readonly record struct HecateStatus(
    HecateFrameKind Kind, int Percent, ChargingState Charging, byte ChargeFlag);

/// <summary>
/// 功 能： 解析 HECATE report 0x04 电量状态帧。
/// 2026/10/19 14:55:00 xliu
/// 帧布局见 docs/hardware/hecate.md §1。0x01 在此品牌表示充电，与英菲克相反。
/// </summary>
internal static class HecateCodec
{
    public const byte StatusReportId = 0x04;
    public const int ReportLength = 64;
    public const int StatusMarkerIndex = 3;
    public const byte StatusMarker = 0x1A;
    public const int PercentIndex = 8;
    public const int ChargeFlagIndex = 9;

    public static HecateStatus Parse(byte[] input)
    {
        if (input.Length < ReportLength) return NotStatus();
        if (input[0] != StatusReportId) return NotStatus();
        if (input[StatusMarkerIndex] != StatusMarker) return NotStatus();

        byte flag = input[ChargeFlagIndex];
        byte pct = input[PercentIndex];

        // 0 和大于 100 是休眠/唤醒瞬态，不能拿去更新读数。
        if (pct is 0 or > 100)
            return new HecateStatus(HecateFrameKind.StatusInvalidPercent, pct, ChargingState.Unknown, flag);

        var charging = flag switch
        {
            0x01 => ChargingState.Charging,
            0x02 => ChargingState.Full,
            0x00 => ChargingState.Discharging,
            _ => ChargingState.Unknown,
        };
        if (pct >= 100) charging = ChargingState.Full;

        return new HecateStatus(HecateFrameKind.Status, pct, charging, flag);
    }

    public static bool IsHeartbeat(byte[] input)
        => input.Length >= 8 && input[0] == StatusReportId && input[1] == 0xAA && input[3] == 0xAA;

    private static HecateStatus NotStatus() =>
        new(HecateFrameKind.NotStatus, 0, ChargingState.Unknown, 0);
}
