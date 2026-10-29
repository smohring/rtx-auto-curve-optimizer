namespace RtxAutoCurveOptimizer.Core;

/// <summary>Identifies a GPU: name, driver version and PCI identifiers.</summary>
public sealed record GpuIdentity(
    string Name, string DriverVersion,
    uint DeviceId, uint SubsystemId, uint RevisionId,
    uint BusNumber);
