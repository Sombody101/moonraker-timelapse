using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentResults;
using Microsoft.AspNetCore.Mvc;
using PrintSpectacle.Models;

namespace PrintSpectacle.Services;

public sealed class TimelapseService(HttpClient _httpClient, ILogger<TimelapseService> _logger) : ITimelapseService
{
    public async Task<Result> TakeSnapshotAsync(ContainerConfiguration config, CaptureRequest payload, CancellationToken token)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(payload.JobID);
            ArgumentException.ThrowIfNullOrWhiteSpace(payload.SnapshotURL);

            await CaptureAsync(payload.JobID, payload.Layer, payload.SnapshotURL, payload.Force, token);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            string error = $"Failed to capture snapshot: {ex.Message}";
            _logger.LogError(ex, error);
            return Result.Fail(error);
        }
    }

    public async Task RenderSnapshotsAsync(ContainerConfiguration config, RenderRequest payload, CancellationToken token)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(payload.JobID);

            await RenderAsync(payload.JobID, payload.Force, token);
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

    public Result<FileStreamResult> GetRenderedJobStream(string jobId)
    {
        string timelapsePath = Path.Combine(GetTimelapseDirectory(jobId), "timelapse.mp4");
        if (!File.Exists(timelapsePath))
        {
            return Result.Fail($"There is no timelapse for job '{jobId}'");
        }

        FileStream fs = File.OpenRead(timelapsePath);
        var fileResult = new FileStreamResult(fs, new Microsoft.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream"))
        {
            FileDownloadName = "timelapse.mp4"
        };

        return Result.Ok(fileResult);
    }

    private async Task CaptureAsync(string jobId, int layer, string cameraUrl, bool force, CancellationToken token)
    {
        JobManager.RenderJobInfo jobHandle = JobManager.GetJob(jobId);

        string jobDirectory = GetCaptureDirectory(jobId);
        _ = Directory.CreateDirectory(jobDirectory);
        string snapshotPath = Path.Combine(jobDirectory, $"{layer}.jpg");

        if (!force && File.Exists(snapshotPath))
        {
            string error = $"Snapshot for layer {layer} already exists for job '{jobId}'";
            jobHandle.SetResult(error, JobManager.JobStatus.Errored);
            throw new InvalidOperationException(error);
        }

        await FetchCaptureAsync(cameraUrl, snapshotPath, token);
    }

    private static async Task RenderAsync(string jobId, bool force, CancellationToken token)
    {
        JobManager.RenderJobInfo jobHandle = JobManager.GetJob(jobId);
        jobHandle.Status = JobManager.JobStatus.Starting;

        string captureDirectory = GetCaptureDirectory(jobId);

        if (!Directory.Exists(captureDirectory))
        {
            MissingJobDirectoryException.Throw(jobId);
        }

        string timelapseDirectory = GetTimelapseDirectory(jobId);
        _ = Directory.CreateDirectory(timelapseDirectory);

        string outputTimelapse = Path.Combine(timelapseDirectory, "timelapse.mp4");

        if (!force && File.Exists(outputTimelapse))
        {
            string error = $"Timelapse already exists for job '{jobId}'";
            jobHandle.SetResult(error, JobManager.JobStatus.Errored);
            throw new InvalidOperationException(error);
        }

        using FfmpegHost ffmpeg = new(captureDirectory, outputTimelapse);

        jobHandle.Status = JobManager.JobStatus.Running;

        try
        {
            await ffmpeg.StartFfmpegAsync(token);
        }
        catch (Exception ex)
        {
            jobHandle.SetResult(ex.Message, JobManager.JobStatus.Errored);
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
        private static readonly ConcurrentDictionary<string, RenderJobInfo> s_currentJobs = [];

        public static RenderJobInfo GetJob(string jobId)
        {
            return GetJobFromID(jobId) ?? StartJob(jobId);
        }

        public static RenderJobInfo GetRequiredJob(string jobId)
        {
            return GetRequiredJobFromID(jobId);
        }

        private static RenderJobInfo StartJob(string jobId)
        {
            var job = new RenderJobInfo(jobId, JobStatus.Starting);
            if (!s_currentJobs.TryAdd(jobId, job))
            {
                DuplicatJobException.Throw(jobId);
            }

            return job;
        }

        public static string GetJobStatus(string jobId)
        {
            RenderJobInfo? job = GetRequiredJobFromID(jobId);

            // Entirely sketchy since it's a private class and may change at any point
            return JsonSerializer.Serialize(job);
        }

        private static RenderJobInfo GetRequiredJobFromID(string jobId)
        {
            RenderJobInfo? job = GetJobFromID(jobId);
            if (job is null)
            {
                NonexistentJobException.Throw(jobId);
            }

            return job;
        }

        private static RenderJobInfo? GetJobFromID(string jobId)
        {
            return s_currentJobs.GetValueOrDefault(jobId);
        }

        public sealed class RenderJobInfo(string jobID, JobStatus jobStatus)
        {
            public static readonly string EmptyJsonPayload = JsonSerializer.Serialize(new RenderJobInfo("none", JobStatus.Invalid));

            [JsonPropertyName("job_id")]
            public string JobID { get; } = jobID;

            [JsonPropertyName("status")]
            public JobStatus Status { get; set; } = jobStatus;

            [JsonPropertyName("result")]
            public string? Result { get; private set; }

            public void SetResult(string result, JobStatus status)
            {
                Result = result;
                Status = status;
            }
        }

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
