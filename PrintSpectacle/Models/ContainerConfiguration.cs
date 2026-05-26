using PrintSpectacle.Services;

namespace PrintSpectacle.Models;

/// <summary>
/// Configurations passed from compose
/// </summary>
public sealed class ContainerConfiguration() : EnvironmentVariableResolver<TimelapseService>()
{
    [EnvVar("THREADS")]
    public int Threads { get; init; } = Environment.ProcessorCount / 2;

    [EnvVar("RENDER_FPS")]
    public int RenderFps { get; init; } = 24;

    [EnvVar("DYNAMIC_FPS")]
    public bool DynamicFps { get; init; } = false;

    [EnvVar("TARGET_RUNTIME")]
    public int TargetRuntime { get; init; } = 10; // Seconds

    [EnvVar("DYNAMIC_FPS_MIN")]
    public int DynamicFpsMin { get; init; } = 5;

    [EnvVar("DYNAMIC_FPS_MAX")]
    public int DynamicFpsMax { get; init; } = 60;
}
