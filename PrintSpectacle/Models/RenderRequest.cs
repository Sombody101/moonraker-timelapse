using System.Text.Json.Serialization;

namespace PrintSpectacle.Models;

public sealed record RenderRequest(
    [property:JsonPropertyName("job_id"), JsonRequired]
    string JobID,

    [property:JsonPropertyName("additional_ffmpeg_args")]
    string? FfmpegAdditionalArgs = null,

    [property:JsonPropertyName("force")]
    bool Force = false
)
{
    public override string ToString()
    {
        string ffmpegArgs = FfmpegAdditionalArgs?.Length > 0
            ? string.Join(' ', FfmpegAdditionalArgs)
            : string.Empty;

        return $"{{ job_id: '{JobID}', force: {Force}, ffmpeg_args: [{ffmpegArgs}] }}";
    }
};
