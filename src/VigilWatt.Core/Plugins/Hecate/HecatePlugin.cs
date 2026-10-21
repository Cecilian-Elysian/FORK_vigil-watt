using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins.Hecate;

/// <summary>
/// 功 能： HECATE 2.4G 鼠标插件，只被动读取状态帧。
/// 2026/10/19 14:55:00 xliu
/// 帧布局见 docs/hardware/hecate.md §1。6 秒内没有任何报文则不认领。
/// </summary>
public sealed class HecatePlugin : BatteryPluginBase
{
    public const ushort HecateVid = 0x35BB;
    public const ushort G5mProPid = 0xD00B;
    public const ushort VendorUsagePage = 0xFF1C;
    public const ushort VendorUsage = 0x0092;

    public override string Name => "Hecate";
    public override string DisplayName => "漫步者电竞";
    public override string DisplayNameEn => "HECATE";
    public override DeviceKind Kind => DeviceKind.Mouse;
    public override bool SupportsFastDiscovery => true;
    protected override TimeSpan ProbeCooldown => TimeSpan.FromSeconds(5);
    protected override bool WritesEnabled => false;
    protected override bool ProbeWrites => false;
    protected override bool SupportsPassiveListen => true;

    /// <summary>只有心跳、没有电量帧时设备仍在；标离线会从列表里消失。</summary>
    protected override bool NullPercentMeansOffline => false;

    public const string DormantProtocol = "2.4G（电量推送休眠：打开一次 HECATE Connect 唤醒）";

    /// <summary>没有查询命令，轮询返回 null 不是失联。静音 60 秒才离线。</summary>
    protected override OfflinePolicy Offline { get; } = new()
    {
        Confirm = TimeSpan.FromSeconds(10),
        Silence = TimeSpan.FromSeconds(60),
        CountPollFailures = false,
    };

    protected override bool IsCandidate(HidDeviceInfo info)
        => info.VendorId == HecateVid && info.ProductId == G5mProPid
           && info.UsagePage == VendorUsagePage && info.Usage == VendorUsage
           && info.InputReportLength >= HecateCodec.ReportLength
           && info.OutputReportLength >= HecateCodec.ReportLength;

    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
        => ProbeAsync(handle).GetAwaiter().GetResult();

    protected override async Task<(string Protocol, int? Percent, ChargingState Charging)?> ProbeAsync(
        HidDeviceHandle handle)
    {
        var heard = await ListenAsync(handle, TimeSpan.FromSeconds(6)).ConfigureAwait(false);
        if (heard.Percent.HasValue) return ("2.4G", heard.Percent, heard.Charging);
        if (!heard.AnyInput) return null;
        return (DormantProtocol, null, ChargingState.Unknown);
    }

    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice _) => null;

    protected override (int? Percent, ChargingState Charging, bool Offline)? TryParsePassiveInput(
        TrackedDevice dev, byte[] input)
    {
        var status = HecateCodec.Parse(input);
        if (status.Kind == HecateFrameKind.NotStatus)
        {
            if (Log.IsEnabled(LogLevel.Debug) && HecateCodec.IsHeartbeat(input))
                Log.Debug($"HECATE 心跳: {Log.Hex(input)}");
            return null;
        }

        if (dev.Status.Protocol != "2.4G")
            dev.Status.Protocol = "2.4G";

        Log.InfoOnChange($"hecate-status:{dev.Status.Id}",
            $"{status.ChargeFlag}|{status.Percent}|{status.Charging}",
            $"HECATE 状态帧: 电量={status.Percent}% 充电标志=0x{status.ChargeFlag:X2} " +
            $"充电状态={status.Charging} 原始={Log.Hex(input)}");

        if (status.Kind == HecateFrameKind.StatusInvalidPercent) return null;
        return (status.Percent, status.Charging, Offline: false);
    }

    private readonly record struct ProbeListen(bool AnyInput, int? Percent, ChargingState Charging);

    private static async Task<ProbeListen> ListenAsync(HidDeviceHandle handle, TimeSpan window)
    {
        bool anyInput = false;
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var slice = remaining < TimeSpan.FromMilliseconds(500) ? remaining : TimeSpan.FromMilliseconds(500);
            if (slice <= TimeSpan.Zero) break;
            var input = await handle.ReadInputAsync(slice).ConfigureAwait(false);
            if (input == null) continue;
            anyInput = true;
            if (Log.IsEnabled(LogLevel.Debug)) Log.Debug($"HECATE 收到报文: {Log.Hex(input)}");

            var status = HecateCodec.Parse(input);
            if (status.Kind == HecateFrameKind.Status)
                return new ProbeListen(true, status.Percent, status.Charging);
        }
        return new ProbeListen(anyInput, null, ChargingState.Unknown);
    }

    protected override string BuildDisplayName(HidDeviceInfo info)
        => string.IsNullOrWhiteSpace(info.Product) ? "HECATE G5M Pro" : info.Product;
}
