using System.Text.Json.Serialization;
using FluentResults;
using Microsoft.AspNetCore.Mvc;
using PrintSpectacle.Models;
using PrintSpectacle.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PrintSpectacle;

public sealed class Program
{
    private static IServiceProvider s_serviceProvider = null!;

    public static T? GetService<T>()
    {
        return s_serviceProvider.GetService<T>();
    }

    public static T GetRequiredService<T>() where T : notnull
    {
        return s_serviceProvider.GetRequiredService<T>();
    }

    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        _ = builder.Services
            .AddProblemDetails()
            .AddOpenApi()
            .AddControllers();

        _ = builder.Logging.ClearProviders();
        _ = builder.Services.AddLogging(builder =>
        {
            const string consoleTemplate = "{Timestamp:HH:mm:ss} {Level:w5} {Message:lj}{NewLine}{Exception}";

            Logger logger = new LoggerConfiguration()
                .WriteTo.Console(outputTemplate: consoleTemplate)
                .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogEventLevel.Warning)
                .CreateLogger();

            Log.Logger = logger;
            _ = builder.AddSerilog(logger);
        });

        var containerConfig = new ContainerConfiguration();
        _ = builder.Services
            .AddHttpClient()
            .AddHostedService<QueuedWorker>()
            .AddSingleton<ITimelapseService, TimelapseService>()
            .AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>()
            .AddSingleton<JobManager>()
            .AddSingleton(containerConfig)
            .AddTransient(provider =>
            {
                IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();
                HttpClient client = factory.CreateClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("PrintSpectacle");
                return client;
            });

        WebApplication app = builder.Build();
        s_serviceProvider = app.Services;

        if (app.Environment.IsDevelopment())
        {
            _ = app.MapOpenApi();
            _ = app.UseDeveloperExceptionPage();
        }

        _ = app.UseAuthorization();
        _ = app.MapControllers();

        if (!Directory.Exists("/data"))
        {
            Console.WriteLine("Failed to find /data. Check if your mounts are correct.");
        }

        _ = app.MapGet("/api/ping", async (HttpRequest req, ILogger<Program> _logger) =>
        {
            string? remote = req.HttpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString();
            _logger.LogInformation("Ping from {Sender}", remote);
            return Results.Ok("pong");
        });

        _ = app.MapPost("/api/timelapse/capture", CaptureAsync);
        _ = app.MapPost("/api/timelapse/render", RenderAsync);

        _ = app.MapGet("/api/timelapse/status/{jobId}", async (string jobId, ITimelapseService service, ILogger<Program> logger) =>
        {
            logger.LogInformation("Status request for job '{Job}'", jobId);
            return Results.Ok(service.GetJobStatus(jobId));
        });

        _ = app.MapGet("/api/timelapse/jobs", (ITimelapseService service, ILogger<Program> logger) =>
        {
            RenderJobInfo[] jobs = [.. service.GetAllJobs()];
            logger.LogInformation("Job dump request. Found {Count} job(s)", jobs.Length);
            return Results.Ok(jobs);
        });

        _ = app.MapGet("/api/timelapse/download/{jobId}", GetDownloadAsync);

        _ = app.MapPatch("api/config", PatchConfigAsync);

        app.Run();
    }

    private static async Task<IResult> CaptureAsync(
        [FromBody] CaptureRequest capture,
        ITimelapseService service,
        ContainerConfiguration containerConfig,
        ILogger<Program> logger,
        CancellationToken token
    )
    {
        logger.LogInformation("Capture request: {Capture}", capture);

        Result result = await service.TakeSnapshotAsync(containerConfig, capture, token);
        if (result.IsFailed)
        {
            IError error = result.Errors[0];
            return Results.InternalServerError(JsonizeError(error.Message));
        }

        return Results.Ok();
    }

    private static async Task<IResult> RenderAsync(
        [FromBody] RenderRequest render,
        IBackgroundTaskQueue queue,
        ITimelapseService service,
        ContainerConfiguration containerConfig,
        ILogger<Program> logger
    )
    {
        logger.LogInformation("Render request: {Render}", render);

        await queue.EnqueueAsync(async token =>
        {
            await service.RenderSnapshotsAsync(containerConfig, render, token);
        });

        return Results.Accepted($"/api/timelapse/status/{render.JobID}");
    }

    private static async Task<IResult> GetDownloadAsync(
        string jobId,
        ITimelapseService service,
        ILogger<Program> logger
    )
    {
        logger.LogInformation("Download request for stream '{Job}'", jobId);
        Result<FileStreamResult> renderStream = service.GetRenderedJobStream(jobId);
        if (renderStream.IsFailed)
        {
            IError error = renderStream.Errors[0];
            return Results.InternalServerError(JsonizeError(error.Message));
        }

        FileStreamResult stream = renderStream.Value;

        return Results.File(
            stream.FileStream,
            stream.ContentType,
            stream.FileDownloadName
        );
    }

    private static async Task<IResult> PatchConfigAsync([FromBody] ConfigPatch configPatch, ContainerConfiguration config, ILogger<Program> logger)
    {
        logger.LogInformation("Config patch request, {Name}: {Value}", configPatch.Name, configPatch.Value);
        // unimplemented
        return Results.Ok();
    }

    private static object JsonizeError(string error)
    {
        // High tech shit. I know.
        return new { error };
    }
}

public sealed record ConfigPatch(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] object Value
);
