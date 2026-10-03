using System.Text.Json.Serialization;

namespace PrintSpectacle.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobStatus
{
    Idle,
    Starting,
    Running,
    Complete,
    Errored,
    Invalid,
}
