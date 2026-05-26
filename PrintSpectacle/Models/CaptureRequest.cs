using System.Text.Json.Serialization;

namespace PrintSpectacle.Models;

public sealed record CaptureRequest(
    [property:JsonPropertyName("job_id"), JsonRequired]
    string JobID,

    [property:JsonPropertyName("layer"), JsonRequired]
    int Layer,

    [property:JsonPropertyName("snapshot_url"), JsonRequired]
    string SnapshotURL,

    [property:JsonPropertyName("force")]
    bool Force = false
);
