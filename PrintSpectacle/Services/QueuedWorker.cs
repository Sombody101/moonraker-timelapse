using System.Threading.Channels;

namespace PrintSpectacle.Services;

public interface IBackgroundTaskQueue
{
    ValueTask EnqueueAsync(Func<CancellationToken, Task> workItem);

    Task<Func<CancellationToken, Task>> DequeueAsync(CancellationToken ct);
}

public class BackgroundTaskQueue : IBackgroundTaskQueue
{
    private readonly Channel<Func<CancellationToken, Task>> _queue = Channel.CreateUnbounded<Func<CancellationToken, Task>>();
    public async ValueTask EnqueueAsync(Func<CancellationToken, Task> workItem)
    {
        await _queue.Writer.WriteAsync(workItem);
    }

    public async Task<Func<CancellationToken, Task>> DequeueAsync(CancellationToken ct)
    {
        return await _queue.Reader.ReadAsync(ct);
    }
}

public class QueuedWorker(IBackgroundTaskQueue _queue, ILogger<QueuedWorker> _logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Func<CancellationToken, Task> work = await _queue.DequeueAsync(stoppingToken);

            try
            {
                await work(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker task failed.");
            }
        }
    }
}
