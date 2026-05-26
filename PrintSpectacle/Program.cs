using System.Text.Json;
using FluentResults;
using Microsoft.AspNetCore.Mvc;
using PrintSpectacle.Models;
using PrintSpectacle.Services;
using Serilog;
using Serilog.Core;

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

        _ = builder.Services.AddControllers();
        _ = builder.Services.AddOpenApi();

        HttpClient httpClient = new();
        httpClient.DefaultRequestHeaders.Clear();
        httpClient.DefaultRequestHeaders.Add("UserAgent", "PrintSpectacle");

        _ = builder.Logging.ClearProviders();
        _ = builder.Services.AddLogging(builder =>
        {
            const string consoleTemplate = "{Timestamp:HH:mm:ss} {Level:w5} {Message:lj}{NewLine}{Exception}";

            Logger logger = new LoggerConfiguration()
                .WriteTo.Console(outputTemplate: consoleTemplate)
                .CreateLogger();

            Log.Logger = logger;
            _ = builder.AddSerilog(logger);
        });

        var containerConfig = new ContainerConfiguration();
        _ = builder.Services
            .AddHostedService<QueuedWorker>()
            .AddSingleton(httpClient)
            .AddSingleton<ITimelapseService, TimelapseService>()
            .AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>()
            .AddSingleton(containerConfig);

        WebApplication app = builder.Build();
        s_serviceProvider = app.Services;

        if (app.Environment.IsDevelopment())
        {
            _ = app.MapOpenApi();
        }

        _ = app.UseAuthorization();
        _ = app.MapControllers();

        if (!Directory.Exists("/data"))
        {
            Console.WriteLine("Failed to find /data. Check if your mounts are correct.");
        }

        _ = app.MapGet("/api/ping", async (ILogger<Program> _logger) =>
        {
            _logger.LogInformation("Ping");
            return Results.Ok("pong");
        });

        _ = app.MapPost("/api/timelapse/capture", async (HttpRequest req, ITimelapseService service, CancellationToken token) =>
        {
            Result<CaptureRequest> captureResult = await ExtractBodyAsync<CaptureRequest>(req);
            if (captureResult.IsFailed)
            {
                return Results.BadRequest(JsonizeError(captureResult.Errors[0].Message));
            }

            CaptureRequest capture = captureResult.Value;

            Result result = await service.TakeSnapshotAsync(containerConfig, capture, token);
            if (result.IsFailed)
            {
                IError error = result.Errors[0];
                return Results.InternalServerError(JsonizeError(error.Message));
            }

            return Results.Ok();
        });

        _ = app.MapPost("/api/timelapse/render", async (HttpRequest req, IBackgroundTaskQueue queue, ITimelapseService service, CancellationToken token) =>
        {
            Result<RenderRequest> renderResult = await ExtractBodyAsync<RenderRequest>(req);
            if (renderResult.IsFailed)
            {
                return Results.BadRequest(JsonizeError(renderResult.Errors[0].Message));
            }

            RenderRequest render = renderResult.Value;

            await queue.EnqueueAsync(async token =>
            {
                await service.RenderSnapshotsAsync(containerConfig, render, token);
            });

            return Results.Accepted($"/api/timelapse/status/{render.JobID}");
        });

        _ = app.MapGet("/api/timelapse/status/{jobId}", async (string jobId, ITimelapseService service) =>
        {
            // Returns raw string, so cannot be wrapped in Results object
            return service.GetJobStatusJson(jobId);
        });

        _ = app.MapGet("/api/timelapse/download/{jobId}", async (string jobId, ITimelapseService service) =>
        {
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
        });

        app.Run();
    }

    private static async Task<Result<T>> ExtractBodyAsync<T>(HttpRequest request)
    {
        try
        {
            using var sr = new StreamReader(request.Body);
            string body = await sr.ReadToEndAsync();
            return JsonSerializer.Deserialize<T>(body) ?? throw new InvalidOperationException("Invalid document.");
        }
        catch (JsonException jex)
        {
            Log.Logger.Error("Unable to parse inbound JSON body: {Message}", jex.Message);
            return Result.Fail($"Invalid JSON format: {jex.Message}");
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "Unable to parse inbound JSON body");
            return Result.Fail("Unexpected error.");
        }
    }

    private static object JsonizeError(string error)
    {
        return new { error };
    }
}
