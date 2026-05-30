using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

public sealed partial class PipelineLoopService
{
    private void NotifyChange()
    {
        try { OnChange?.Invoke(); }
        catch (Exception ex) { _logger.Warning(ex, "PipelineLoopService OnChange handler threw"); }
    }
}
