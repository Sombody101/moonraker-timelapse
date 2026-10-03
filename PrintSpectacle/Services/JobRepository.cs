using LiteDB;
using PrintSpectacle.Models;

namespace PrintSpectacle.Services;

public sealed class JobRepository : IDisposable
{
    private const string JOB_COLUMN_ID = "jobs";

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<InternalRenderJobInfo> _jobCollection;

    public JobRepository()
    {
        _db = new LiteDatabase($"Filename={ConstPaths.JOBS_SAVE_FILE};Connection=direct");
        _db.Mapper.Entity<InternalRenderJobInfo>().Id(r => r.JobID, autoId: false);
        _jobCollection = _db.GetCollection<InternalRenderJobInfo>(JOB_COLUMN_ID);
    }

    public void UpsertJob(InternalRenderJobInfo job)
    {
        _ = _jobCollection.Upsert(job);
    }

    public void CreateJob(InternalRenderJobInfo job)
    {
        _ = _jobCollection.Insert(job);
    }

    public InternalRenderJobInfo? GetJob(string jobId)
    {
        return _jobCollection.FindById(jobId);
    }

    public IEnumerable<InternalRenderJobInfo> GetAllJobs()
    {
        return [.. _jobCollection.FindAll()];
    }

    public ILiteCollection<InternalRenderJobInfo> GetJobCollection() => _jobCollection;

    public void Dispose()
    {
        _db?.Dispose();
    }
}
