using System.Text.Json;
using System.Text.Json.Serialization;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure.Persistence;

/// <summary>
/// Custom JSON converter that maintains backward-compatible flat serialization for
/// <see cref="PipelineConfiguration"/>. Reads and writes all properties at the top level
/// regardless of which sub-config record they belong to.
/// </summary>
internal sealed class PipelineConfigurationJsonConverter : JsonConverter<PipelineConfiguration>
{
    public override PipelineConfiguration Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected StartObject token");

        var retry = new RetryConfiguration();
        var workspace = new WorkspaceConfiguration();
        var externalCi = new ExternalCiConfiguration();
        var closedLoop = new ClosedLoopConfiguration();
        var agent = new AgentConfiguration();
        var commit = new CommitConfiguration();
        var codeReview = new CodeReviewConfiguration();

        int issuePageSize = 25;
        string analysisPrompt = PipelineConfiguration.DefaultAnalysisPrompt;
        string implementationPrompt = PipelineConfiguration.DefaultImplementationPrompt;
        IReadOnlyDictionary<string, string> lastUsedProviderIds = new Dictionary<string, string>();
        IReadOnlyList<PipelineJobTemplate> pipelineJobTemplates = Array.Empty<PipelineJobTemplate>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected PropertyName token");

            var propertyName = reader.GetString()!;
            reader.Read();

            switch (propertyName)
            {
                // RetryConfiguration
                case "maxRetries":
                    retry = retry with { MaxRetries = reader.GetInt32() };
                    break;
                case "maxAnalysisRetries":
                    retry = retry with { MaxAnalysisRetries = reader.GetInt32() };
                    break;
                case "agentTimeout":
                    retry = retry with { AgentTimeout = ReadTimeSpan(ref reader) };
                    break;
                case "stallWarningInterval":
                    retry = retry with { StallWarningInterval = ReadTimeSpan(ref reader) };
                    break;
                case "stallPollInterval":
                    retry = retry with { StallPollInterval = ReadTimeSpan(ref reader) };
                    break;

                // WorkspaceConfiguration
                case "workspaceBaseDirectory":
                    workspace = workspace with { WorkspaceBaseDirectory = reader.GetString()! };
                    break;
                case "failedWorkspaceRetentionDays":
                    workspace = workspace with { FailedWorkspaceRetentionDays = reader.GetInt32() };
                    break;

                // ExternalCiConfiguration
                case "externalCiEnabled":
                    externalCi = externalCi with { Enabled = reader.GetBoolean() };
                    break;
                case "externalCiTimeout":
                    externalCi = externalCi with { Timeout = ReadTimeSpan(ref reader) };
                    break;
                case "externalCiPollInterval":
                    externalCi = externalCi with { PollInterval = ReadTimeSpan(ref reader) };
                    break;

                // ClosedLoopConfiguration
                case "closedLoopPollInterval":
                    closedLoop = closedLoop with { PollInterval = ReadTimeSpan(ref reader) };
                    break;
                case "closedLoopMaxRunsPerCycle":
                    closedLoop = closedLoop with { MaxRunsPerCycle = reader.GetInt32() };
                    break;
                case "closedLoopMaxConsecutivePollFailures":
                    closedLoop = closedLoop with { MaxConsecutivePollFailures = reader.GetInt32() };
                    break;
                case "closedLoopMaxBackoffInterval":
                    closedLoop = closedLoop with { MaxBackoffInterval = ReadTimeSpan(ref reader) };
                    break;
                case "closedLoopMaxPagesToFetch":
                    closedLoop = closedLoop with { MaxPagesToFetch = reader.GetInt32() };
                    break;

                // AgentConfiguration
                case "defaultRequiredAgentLabels":
                    agent = agent with { DefaultRequiredAgentLabels = reader.TokenType == JsonTokenType.Null ? null : reader.GetString() };
                    break;
                case "brainPushMaxRetries":
                    agent = agent with { BrainPushMaxRetries = reader.GetInt32() };
                    break;
                case "agentDisconnectGracePeriod":
                    agent = agent with { AgentDisconnectGracePeriod = ReadTimeSpan(ref reader) };
                    break;
                case "outputBufferCapacity":
                    agent = agent with { OutputBufferCapacity = reader.GetInt32() };
                    break;
                case "brainReadOnly":
                    agent = agent with { BrainReadOnly = reader.GetBoolean() };
                    break;

                // CommitConfiguration
                case "blacklistedPaths":
                    commit = commit with { BlacklistedPaths = JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? new List<string>() };
                    break;
                case "blacklistMode":
                    commit = commit with { BlacklistMode = JsonSerializer.Deserialize<BlacklistMode>(ref reader, options) };
                    break;

                // CodeReviewConfiguration (nested object — already existed as nested)
                case "codeReview":
                    codeReview = JsonSerializer.Deserialize<CodeReviewConfiguration>(ref reader, options) ?? new();
                    break;

                // Top-level properties
                case "issuePageSize":
                    issuePageSize = reader.GetInt32();
                    break;
                case "analysisPrompt":
                    analysisPrompt = reader.GetString() ?? PipelineConfiguration.DefaultAnalysisPrompt;
                    break;
                case "implementationPrompt":
                    implementationPrompt = reader.GetString() ?? PipelineConfiguration.DefaultImplementationPrompt;
                    break;
                case "lastUsedProviderIds":
                    lastUsedProviderIds = JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, options) ?? new Dictionary<string, string>();
                    break;
                case "pipelineJobTemplates":
                    pipelineJobTemplates = JsonSerializer.Deserialize<List<PipelineJobTemplate>>(ref reader, options) ?? new List<PipelineJobTemplate>();
                    break;

                default:
                    reader.Skip();
                    break;
            }
        }

        return new PipelineConfiguration
        {
            Retry = retry,
            Workspace = workspace,
            ExternalCi = externalCi,
            ClosedLoop = closedLoop,
            Agent = agent,
            Commit = commit,
            CodeReview = codeReview,
            IssuePageSize = issuePageSize,
            AnalysisPrompt = analysisPrompt,
            ImplementationPrompt = implementationPrompt,
            LastUsedProviderIds = lastUsedProviderIds,
            PipelineJobTemplates = pipelineJobTemplates,
        };
    }

    public override void Write(Utf8JsonWriter writer, PipelineConfiguration value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        // RetryConfiguration
        writer.WriteNumber("maxRetries", value.Retry.MaxRetries);
        writer.WriteNumber("maxAnalysisRetries", value.Retry.MaxAnalysisRetries);
        WriteTimeSpan(writer, "agentTimeout", value.Retry.AgentTimeout);
        WriteTimeSpan(writer, "stallWarningInterval", value.Retry.StallWarningInterval);
        WriteTimeSpan(writer, "stallPollInterval", value.Retry.StallPollInterval);

        // WorkspaceConfiguration
        writer.WriteString("workspaceBaseDirectory", value.Workspace.WorkspaceBaseDirectory);
        writer.WriteNumber("failedWorkspaceRetentionDays", value.Workspace.FailedWorkspaceRetentionDays);

        // ExternalCiConfiguration
        writer.WriteBoolean("externalCiEnabled", value.ExternalCi.Enabled);
        WriteTimeSpan(writer, "externalCiTimeout", value.ExternalCi.Timeout);
        WriteTimeSpan(writer, "externalCiPollInterval", value.ExternalCi.PollInterval);

        // ClosedLoopConfiguration
        WriteTimeSpan(writer, "closedLoopPollInterval", value.ClosedLoop.PollInterval);
        writer.WriteNumber("closedLoopMaxRunsPerCycle", value.ClosedLoop.MaxRunsPerCycle);
        writer.WriteNumber("closedLoopMaxConsecutivePollFailures", value.ClosedLoop.MaxConsecutivePollFailures);
        WriteTimeSpan(writer, "closedLoopMaxBackoffInterval", value.ClosedLoop.MaxBackoffInterval);
        writer.WriteNumber("closedLoopMaxPagesToFetch", value.ClosedLoop.MaxPagesToFetch);

        // AgentConfiguration
        if (value.Agent.DefaultRequiredAgentLabels is not null)
            writer.WriteString("defaultRequiredAgentLabels", value.Agent.DefaultRequiredAgentLabels);
        else
            writer.WriteNull("defaultRequiredAgentLabels");
        writer.WriteNumber("brainPushMaxRetries", value.Agent.BrainPushMaxRetries);
        WriteTimeSpan(writer, "agentDisconnectGracePeriod", value.Agent.AgentDisconnectGracePeriod);
        writer.WriteNumber("outputBufferCapacity", value.Agent.OutputBufferCapacity);
        writer.WriteBoolean("brainReadOnly", value.Agent.BrainReadOnly);

        // CommitConfiguration
        writer.WritePropertyName("blacklistedPaths");
        JsonSerializer.Serialize(writer, value.Commit.BlacklistedPaths, options);
        writer.WritePropertyName("blacklistMode");
        JsonSerializer.Serialize(writer, value.Commit.BlacklistMode, options);

        // CodeReviewConfiguration (nested object)
        writer.WritePropertyName("codeReview");
        JsonSerializer.Serialize(writer, value.CodeReview, options);

        // Top-level
        writer.WriteNumber("issuePageSize", value.IssuePageSize);
        writer.WriteString("analysisPrompt", value.AnalysisPrompt);
        writer.WriteString("implementationPrompt", value.ImplementationPrompt);
        writer.WritePropertyName("lastUsedProviderIds");
        JsonSerializer.Serialize(writer, value.LastUsedProviderIds, options);
        writer.WritePropertyName("pipelineJobTemplates");
        JsonSerializer.Serialize(writer, value.PipelineJobTemplates, options);

        writer.WriteEndObject();
    }

    private static TimeSpan ReadTimeSpan(ref Utf8JsonReader reader)
    {
        var value = reader.GetString();
        return value is not null
            ? TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
            : default;
    }

    private static void WriteTimeSpan(Utf8JsonWriter writer, string propertyName, TimeSpan value)
    {
        writer.WriteString(propertyName, value.ToString("c", System.Globalization.CultureInfo.InvariantCulture));
    }
}
