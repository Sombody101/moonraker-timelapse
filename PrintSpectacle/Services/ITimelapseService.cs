using FluentResults;
using Microsoft.AspNetCore.Mvc;
using PrintSpectacle.Models;

namespace PrintSpectacle.Services;

public interface ITimelapseService
{
    public Task<Result> TakeSnapshotAsync(ContainerConfiguration config, CaptureRequest payload, CancellationToken token);

    public Task RenderSnapshotsAsync(ContainerConfiguration config, RenderRequest payload, CancellationToken token);

    public RenderJobInfo GetJobStatus(string jobId);

    public IEnumerable<RenderJobInfo> GetAllJobs();

    public Result<FileStreamResult> GetRenderedJobStream(string jobId);
}
