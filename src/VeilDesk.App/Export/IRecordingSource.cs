using VeilDesk.Core.Geometry;

namespace VeilDesk.App.Export;

/// <summary>Local frame ownership boundary shared by the live engine and deterministic recorder tests.</summary>
public interface IRecordingSource
{
    bool IsRunning { get; }
    IReadOnlyList<MonitorDescriptor> GetActiveMonitors();
    RecordingFrame? TryGrabRecordingFrame(string monitorId);
}
