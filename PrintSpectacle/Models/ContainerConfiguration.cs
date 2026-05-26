using PrintSpectacle.Services;

namespace PrintSpectacle.Models;

/// <summary>
/// Configurations passed from compose
/// </summary>
public sealed class ContainerConfiguration() : EnvironmentVariableResolver<TimelapseService>()
{
    [EnvVar("THREADS")]
    public int Threads { get; init; } = Environment.ProcessorCount / 2;
}
