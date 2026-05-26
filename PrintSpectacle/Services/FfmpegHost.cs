using System.Diagnostics;
using System.Text;
using PrintSpectacle.Models;

namespace PrintSpectacle.Services;

public sealed class FfmpegHost : IDisposable
{
    private readonly Process _ffmpegProcess;

    public FfmpegHost(string sourceDirectory, string outputPath)
    {
        int frameCount = GetFrameCount(sourceDirectory);
        string args = ArgumentProvider.CreateFfmpegArgs(outputPath, frameCount);

        _ffmpegProcess = new Process()
        {
            StartInfo = new()
            {
                FileName = "ffmpeg",
                Arguments = args,
                UseShellExecute = false,
                WorkingDirectory = sourceDirectory,
                RedirectStandardError = true,
            }
        };
    }

    public async Task StartFfmpegAsync(CancellationToken token)
    {
        StringBuilder stderrBuff = new();
        _ffmpegProcess.ErrorDataReceived += (sender, args) =>
        {
            if (args.Data is not null)
            {
                stderrBuff.AppendLine(args.Data);
            }
        };

        _ = _ffmpegProcess.Start();
        _ffmpegProcess.BeginErrorReadLine();

        await _ffmpegProcess.WaitForExitAsync(token);

        if (_ffmpegProcess.ExitCode is not 0)
        {
            string stderr = stderrBuff.ToString();
            throw new FfmpegFailureException(stderr);
        }
    }

    public void Dispose()
    {
        _ffmpegProcess?.Dispose();
    }

    private int GetFrameCount(string frameDirectory)
    {
        return Directory.GetFiles(frameDirectory).Length;
    }

    private static class ArgumentProvider
    {
        private const string ARGS_SW = "-loglevel error -framerate {2} -i \"%6d.jpg\" -threads {1} -g 5 -c:v libx264 -preset medium -crf 23 -pix_fmt yuv420p -y {0}";

        [Obsolete]
        private const string
            ARGS_QSV = "-loglevel error -framerate 30 -i \"%d.jpg\" -vf \"format=nv12,hwupload\" -g 150 -c:v h264_qsv -preset medium -global_quality 23 -look_ahead 1 -b:v 0 -maxrate 0 -bufsize 0 -pix_fmt nv12 -y {0}",
            ARGS_AMF = "-loglevel error -framerate 30 -i \"%d.jpg\" -vf \"format=nv12,hwupload\" -g 150 -c:v h264_amf -usage transcoding -quality balanced -rc cqp -q 23 -pix_fmt nv12 -y {0}";

        private static CpuVendor? s_cachedVendor;

        public static string CreateFfmpegArgs(string outputPath, int frameCount)
        {
            ContainerConfiguration config = Program.GetRequiredService<ContainerConfiguration>();
            int targetFps = config.DynamicFps
                ? CalculateTargetFps(frameCount, config.TargetRuntime, config.DynamicFpsMin, config.DynamicFpsMax)
                : config.RenderFps;

            string argsFormatter = GetArgsString();
            return string.Format(argsFormatter, outputPath, config.Threads, targetFps);
        }

        private static int CalculateTargetFps(int frameCount, int target, int min, int max)
        {
            return Math.Max(Math.Min(frameCount / target, max), min);
        }

        private static string GetArgsString()
        {
            // WIP
            return ARGS_SW;
        }

        private static CpuVendor GetCpuVendor()
        {
            using FileStream fs = File.OpenRead("/proc/cpuinfo");
            using StreamReader sr = new(fs);

            _ = sr.ReadLine();
            string vendorLine = sr.ReadLine()
                ?? throw new InvalidOperationException("Failed to get CPU vendor.");

            int index = vendorLine.LastIndexOf(':');
            if (index is -1)
            {
                throw new InvalidOperationException("Malformed /proc/cpuinfo"); // More likely to be a me issue, but it's fun to deflect blame.
            }

            string vendor = vendorLine[(index + 2)..].Trim();
            return vendor switch
            {
                "GenuineIntel" => CpuVendor.Intel,
                "AuthenticAMD" => CpuVendor.AMD,
                _ => throw new UnknownHardwareException(vendor),
            };
        }

        private enum CpuVendor
        {
            Intel,
            AMD,
        }
    }
}

public sealed class FfmpegFailureException(string stderr) : Exception($"ffmpeg failure: {stderr}");
public sealed class UnknownHardwareException(string foundVendor) : Exception($"Unknown hardware '{foundVendor}'. Only Intel QSV and AMD AMF are supported.");
