using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrintSpectacle.Models;

namespace PrintSpectacle.Services;

public sealed class TimelapseService(HttpClient _httpClient, ILogger<TimelapseService> _logger) : ITimelapseService
{
    /*
     * Required actions:
     *  Take snapshot
     *  Render snapshots
     */

    public async Task TakeSnapshotAsync(ContainerConfiguration config, CaptureRequest payload, CancellationToken token)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(payload.JobID);
            ArgumentException.ThrowIfNullOrWhiteSpace(payload.SnapshotURL);

            await CaptureAsync(payload.JobID, payload.Layer, payload.SnapshotURL, token);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to capture snapshot: {Message}", ex.Message);
        }
    }

    public async Task RenderSnapshotsAsync(ContainerConfiguration config, RenderRequest payload, CancellationToken token)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(payload.JobID);

            await RenderAsync(payload.JobID);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to render snapshots for job {JobId}", payload.JobID);
        }
    }

    public string GetJobStatusJson(string jobId)
    {
        try
        {
            return JobManager.GetJobStatus(jobId);
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to fetch job stats for {JobID}: {Message}", jobId, ex.Message);
            return JobManager.RenderJobInfo.EmptyJsonPayload;
        }
    }

    private async Task CaptureAsync(string jobId, int layer, string cameraUrl, CancellationToken token)
    {
        using JobManager.RenderJobInfo jobHandle = JobManager.For(jobId);

        string jobDirectory = GetCaptureDirectory(jobId);
        _ = Directory.CreateDirectory(jobDirectory);
        string snapshotPath = Path.Combine(jobDirectory, $"{layer}.jpg");

        await FetchCaptureAsync(cameraUrl, snapshotPath, token);
    }

    private async Task RenderAsync(string jobId)
    {
        using JobManager.RenderJobInfo jobHandle = JobManager.For(jobId);
        jobHandle.Status = JobManager.JobStatus.Starting;

        string captureDirectory = GetCaptureDirectory(jobId);

        if (!Directory.Exists(captureDirectory))
        {
            MissingJobDirectoryException.Throw(jobId);
        }

        string timelapseDirectory = GetTimelapseDirectory(jobId);
        Directory.CreateDirectory(timelapseDirectory);

        string outputTimelapse = Path.Combine(timelapseDirectory, "timelapse.mp4");

        FfmpegHost ffmpeg = new(captureDirectory, outputTimelapse);

        jobHandle.Status = JobManager.JobStatus.Running;

        try
        {
            await ffmpeg.StartFfmpegAsync();
        }
        catch (Exception ex)
        {
            jobHandle.Status = JobManager.JobStatus.Errored;
            jobHandle.SetResult(ex.Message);
            throw;
        }

        jobHandle.Status = JobManager.JobStatus.Complete;
    }

    private async Task FetchCaptureAsync(string url, string outputPath, CancellationToken token)
    {
        using Stream snapshotStream = await _httpClient.GetStreamAsync(url, token);
        using FileStream fs = new(outputPath, FileMode.Create, FileAccess.Write);
        await snapshotStream.CopyToAsync(fs, token);
    }

    private static string GetCaptureDirectory(string jobId)
    {
        return Path.Combine(ConstPaths.SNAPSHOT_DIRECTORY, jobId);
    }

    private static string GetTimelapseDirectory(string jobId)
    {
        return Path.Combine(ConstPaths.TIMELAPSE_OUTPUT, jobId);
    }

    private static class JobManager
    {
        private static readonly HashSet<RenderJobInfo> s_currentJobs = [];

        public static RenderJobInfo For(string jobId)
        {
            if (s_currentJobs.Any(j => j.JobID == jobId))
            {
                DuplicatJobException.Throw(jobId);
            }

            return new RenderJobInfo(jobId, JobStatus.Starting);
        }

        public static string GetJobStatus(string jobId)
        {
            RenderJobInfo? job = GetJobFromID(jobId);

            // Entirely sketchy since it's a private class and may change at any point
            return JsonSerializer.Serialize(job);
        }

        private static RenderJobInfo GetJobFromID(string jobId)
        {
            RenderJobInfo? job = s_currentJobs.FirstOrDefault(j => j.JobID == jobId);

            if (job is null)
            {
                NonexistentJobException.Throw(jobId);
            }

            return job;
        }

        public sealed class RenderJobInfo(string jobID, JobStatus jobStatus) : IDisposable
        {
            public static readonly string EmptyJsonPayload = JsonSerializer.Serialize(new RenderJobInfo("none", JobStatus.Invalid));

            [JsonPropertyName("job_id")]
            public string JobID { get; } = jobID;

            [JsonPropertyName("status")]
            public JobStatus Status { get; set; } = jobStatus;

            [JsonPropertyName("result")]
            public string? Result { get; private set; }

            public void SetResult(string result)
            {
                if (Result is not null && result is not null)
                {
                    Result = result;
                }
            }

            public void Dispose()
            {
                _ = s_currentJobs.Remove(this);
            }
        }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public enum JobStatus
        {
            Starting,
            Running,
            Complete,
            Errored,
            Invalid,
        }
    }

    public sealed class DuplicatJobException(string jobId) : Exception($"Attempt to start duplicate job {jobId}")
    {
        [DoesNotReturn]
        public static void Throw(string jobId)
        {
            throw new DuplicatJobException(jobId);
        }
    }

    public sealed class MissingJobDirectoryException(string jobId) : Exception($"Attempted to start job {jobId}, but its directory could not be found")
    {
        [DoesNotReturn]
        public static void Throw(string jobId)
        {
            throw new MissingJobDirectoryException(jobId);
        }
    }

    public sealed class NonexistentJobException(string jobId) : Exception($"Failed to find job {jobId}")
    {
        [DoesNotReturn]
        public static void Throw(string jobId)
        {
            throw new NonexistentJobException(jobId);
        }
    }
}
