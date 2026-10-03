using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using FluentResults;
using LiteDB;
using Microsoft.AspNetCore.Mvc;
using PrintSpectacle.Models;
using static PrintSpectacle.Services.TimelapseService;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace PrintSpectacle.Services;

public sealed class TimelapseService(
    HttpClient _httpClient,
    JobManager _jobManager,
    ILogger<TimelapseService> _logger) : ITimelapseService
{
    public async Task<Result> TakeSnapshotAsync(ContainerConfiguration config, CaptureRequest payload, CancellationToken token)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(payload.JobID);
            ArgumentException.ThrowIfNullOrWhiteSpace(payload.SnapshotURL);

            await CaptureAsync(SanitizeJobId(payload.JobID), payload.Layer, payload.SnapshotURL, payload.Force, token);
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

            await RenderAsync(SanitizeJobId(payload.JobID), payload.Force, token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to render snapshots for job {JobId}", payload.JobID);
        }
    }

    public RenderJobInfo GetJobStatus(string jobId)
    {
        try
        {
            return ((RenderJobInfo)_jobManager.GetJob(SanitizeJobId(jobId)).Job) with { };
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to fetch job stats for {JobID}: {Message}", jobId, ex.Message);
            return InternalRenderJobInfo.EmptyPayload;
        }
    }

    public IEnumerable<RenderJobInfo> GetAllJobs()
    {
        return _jobManager.GetJobs().Select(x => ((RenderJobInfo)x) with { });
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
        using JobScope scope = _jobManager.GetJob(jobId);
        InternalRenderJobInfo jobHandle = scope.Job;

        try
        {
            string jobDirectory = GetCaptureDirectory(jobId);
            _ = Directory.CreateDirectory(jobDirectory);
            string snapshotPath = Path.Combine(jobDirectory, $"{layer.ToString().PadLeft(6, '0')}.jpg");

            if (!force && File.Exists(snapshotPath))
            {
                string error = $"Snapshot for layer {layer} already exists for job '{jobId}'";
                jobHandle.SetResult(error, JobStatus.Errored);
                throw new InvalidOperationException(error);
            }

            if (jobHandle.LastFrameIndex + 1 != layer)
            {
                _logger.LogWarning("Capture request dictates for the capture of frame {NewLayer}, but the previous frame was {PrevLayer} (difference of {Diff}). Skipping layer count and proceeding.",
                    layer,
                    jobHandle.LastFrameIndex,
                    Math.Abs(layer - jobHandle.LastFrameIndex));
            }

            await FetchCaptureAsync(cameraUrl, snapshotPath, token);
            jobHandle.LastFrameIndex = layer;

            jobHandle.SetResult(null, JobStatus.Idle);
        }
        catch (Exception ex)
        {
            jobHandle.SetResult(ex.Message, JobStatus.Errored);
        }
    }

    private async Task RenderAsync(string jobId, bool force, CancellationToken token)
    {
        using JobScope scope = _jobManager.GetJob(jobId);
        InternalRenderJobInfo jobHandle = scope.Job;

        jobHandle.Status = JobStatus.Starting;

        try
        {
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
                jobHandle.SetResult(error, JobStatus.Errored);
                throw new InvalidOperationException(error);
            }

            _logger.LogInformation("Starting render on: {Dir}", captureDirectory);

            using FfmpegHost ffmpeg = new(captureDirectory, outputTimelapse);

            DuplicateFinalFrame(captureDirectory);

            jobHandle.Status = JobStatus.Running;

            Stopwatch sw = Stopwatch.StartNew();
            await ffmpeg.StartFfmpegAsync(token);
            sw.Stop();

            string statusMessage = $"Render took {sw.ElapsedMilliseconds}ms";

            _logger.LogInformation(statusMessage);
            jobHandle.SetResult(statusMessage, JobStatus.Complete);
        }
        catch (Exception ex)
        {
            jobHandle.SetResult(ex.Message, JobStatus.Errored);
            throw;
        }
    }

    private static void DuplicateFinalFrame(string directory)
    {
        const string INFLATE_FILE = ".inflate";

        string inflateFile = Path.Combine(directory, INFLATE_FILE);
        if (File.Exists(inflateFile))
        {
            return;
        }

        string lastFrame = Directory.GetFiles(directory, "*.jpg")
            .Select(s => s.Replace(directory, string.Empty).ToString())
            .OrderByDescending(f => f)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Failed to get final layer for duplication");

        lastFrame = lastFrame.TrimStart('/');
        if (!int.TryParse(lastFrame.Replace(".jpg", string.Empty), out int layer))
        {
            throw new InvalidOperationException("Unable to get layer number for final layer duplication");
        }

        string sourceFile = Path.Combine(directory, lastFrame);
        int target = layer + 5;
        layer++;
        for (; layer <= target; ++layer)
        {
            string duplicateFrame = Path.Combine(directory, $"{layer.ToString().PadLeft(6, '0')}.jpg");
            File.Copy(sourceFile, duplicateFrame);
        }

        File.Create(inflateFile).Close();
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

    private static string SanitizeJobId(string jobId)
    {
        // I'm sure this is [not] enough...
        return jobId
            .TrimStart('/')
            .Replace('/', '_');
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

public interface IJobDispatch
{
    void DisposeJob(InternalRenderJobInfo instance);
}

public sealed class JobManager : IDisposable, IJobDispatch
{
    private readonly JobRepository _jobRepo = new();

    public JobScope GetJob(string jobId)
    {
        InternalRenderJobInfo job = GetOrCreateJob(jobId);
        return new(this, job);
    }

    public JobScope GetRequiredJob(string jobId)
    {
        InternalRenderJobInfo job = GetRequiredJobFromID(jobId);
        return new(this, job);
    }

    public IEnumerable<InternalRenderJobInfo> GetJobs()
    {
        return _jobRepo.GetAllJobs();
    }

    public string GetJobStatus(string jobId)
    {
        InternalRenderJobInfo? job = GetRequiredJobFromID(jobId);

        // Entirely sketchy since it's a private class and may change at any point
        return JsonSerializer.Serialize(job);
    }

    private InternalRenderJobInfo GetOrCreateJob(string jobId)
    {
        return GetJobFromID(jobId) ?? new() { JobID = jobId };
    }

    private InternalRenderJobInfo GetRequiredJobFromID(string jobId)
    {
        InternalRenderJobInfo? job = GetJobFromID(jobId);
        if (job is null)
        {
            NonexistentJobException.Throw(jobId);
        }

        return job;
    }

    private InternalRenderJobInfo? GetJobFromID(string jobId)
    {
        return _jobRepo.GetJob(jobId);
    }

    public void Dispose()
    {
        _jobRepo?.Dispose();
    }

    public void DisposeJob(InternalRenderJobInfo instance)
    {
        _jobRepo.UpsertJob(instance);
    }
}

public readonly struct JobScope : IDisposable
{
    private readonly IJobDispatch _dispatcher;

    public InternalRenderJobInfo Job { get; }

    internal JobScope(IJobDispatch dispatcher, InternalRenderJobInfo job)
    {
        _dispatcher = dispatcher;
        Job = job;
    }

    public void Dispose()
    {
        _dispatcher.DisposeJob(Job);
    }
}
