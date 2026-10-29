namespace RtxAutoCurveOptimizer.Core;

/// <summary>Reasons the driver currently limits the graphics clock (NVML clock event reasons).</summary>
[Flags]
public enum ClockLimitReasons : ulong
{
    None = 0,
    GpuIdle = 0x1,
    ApplicationClocks = 0x2,
    SwPowerCap = 0x4,
    HwSlowdown = 0x8,
    SyncBoost = 0x10,
    SwThermalSlowdown = 0x20,
    HwThermalSlowdown = 0x40,
    HwPowerBrakeSlowdown = 0x80,
    DisplayClock = 0x100,
}

/// <summary>One telemetry sample; voltage in µV, power in W.</summary>
public readonly record struct Telemetry(
    DateTimeOffset Timestamp,
    int GraphicsClockMhz, int MemoryClockMhz, int VoltageUv,
    double PowerW, double PowerLimitW, int TemperatureC,
    int UtilizationPercent, ClockLimitReasons LimitReasons);
