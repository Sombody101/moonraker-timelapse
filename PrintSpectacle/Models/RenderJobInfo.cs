using System.Text.Json.Serialization;
using LiteDB;

namespace PrintSpectacle.Models;

public record RenderJobInfo
{
    public RenderJobInfo()
    {
    }

    [BsonId]
    [JsonPropertyName("job_id")]
    public required string JobID { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public JobStatus Status { get; set; } = JobStatus.Starting;

    [JsonInclude]
    [JsonPropertyName("result")]
    public string? Result { get; set; }

    [JsonPropertyName("last_frame")]
    public int LastFrameIndex { get; set; }

    public void SetResult(string result, JobStatus status)
    {
        Result = result;
        Status = status;
    }
}

public record InternalRenderJobInfo : RenderJobInfo
{
    public static readonly InternalRenderJobInfo EmptyPayload = new InternalRenderJobInfo()
    {
        JobID = "none",
        Status = JobStatus.Invalid
    };
}
