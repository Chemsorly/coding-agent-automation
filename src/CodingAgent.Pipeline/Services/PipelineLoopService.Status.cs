using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

public sealed partial class PipelineLoopService
{
    private void NotifyChange()
    {
        try { OnChange?.Invoke(); }
        catch (Exception ex) { _logger.Warning(ex, "PipelineLoopService OnChange handler threw"); }
    }
}
