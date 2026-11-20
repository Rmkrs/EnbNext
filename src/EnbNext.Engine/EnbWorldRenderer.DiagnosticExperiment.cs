using System.Diagnostics;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly string? _diagnosticExperimentControl =
        Environment.GetEnvironmentVariable("ENB_EXPERIMENT_DIAGNOSTIC_CONTROL");
    private long _diagnosticModePollAfter, _detailedDiagnosticAfter;
    private bool _sampleDiagnosticReceipts = true;
    private long _cadenceAllocatedBytes;
    private LiveFrameCadenceStatus? _cachedStatusCadence;
    private LiveWorldW3dStatus? _cachedStatusReceipt;
    private string? _cachedStatusDevice, _cachedStatusText;

    private bool DetailedDiagnosticsDue(long timestamp)
    {
        if (_diagnosticExperimentControl is not null && timestamp >= _diagnosticModePollAfter)
        {
            _diagnosticModePollAfter = timestamp + Stopwatch.Frequency / 2;
            try { _sampleDiagnosticReceipts = File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(_diagnosticExperimentControl)).Trim() == "1"; }
            catch (IOException) { _sampleDiagnosticReceipts = false; }
            catch (UnauthorizedAccessException) { _sampleDiagnosticReceipts = false; }
        }
        if (!_sampleDiagnosticReceipts) return true;
        if (timestamp < _detailedDiagnosticAfter) return false;
        _detailedDiagnosticAfter = timestamp + Stopwatch.Frequency;
        return true;
    }

    // UI-thread getter only. Cache the concatenation, never the publication
    // inputs: errors and fresh cadence invalidate it by reference immediately.
    private string ReadCombinedStatus(LiveFrameCadenceStatus cadence, string device, LiveWorldW3dStatus receipt)
    {
        if (!_sampleDiagnosticReceipts) return cadence.Text + "\n" + device + "\n" + receipt.Text;
        if (!ReferenceEquals(cadence, _cachedStatusCadence) || !ReferenceEquals(device, _cachedStatusDevice) ||
            !ReferenceEquals(receipt, _cachedStatusReceipt) || _cachedStatusText is null)
        {
            _cachedStatusCadence = cadence;
            _cachedStatusDevice = device;
            _cachedStatusReceipt = receipt;
            _cachedStatusText = cadence.Text + "\n" + device + "\n" + receipt.Text;
        }
        return _cachedStatusText;
    }
}
