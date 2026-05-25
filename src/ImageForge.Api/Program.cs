using System.Reflection;
using ImageForge.Api.Endpoints;
using ImageForge.Api.Hubs;
using ImageForge.Api.Services;
using ImageForge.Shared.Messaging;
using ImageForge.Shared.Persistence;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "ImageForge API",
        Version = "v1",
        Description = "Asynchronous image processing service. Upload an image, " +
                      "receive a task id, follow progress over SignalR, download " +
                      "the converted file when done."
    });

    // Pull in XML doc comments emitted by the build for richer Swagger UI.
    var xml = Path.Combine(AppContext.BaseDirectory,
        $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
    if (File.Exists(xml))
    {
        c.IncludeXmlComments(xml);
    }
});

// Cap multipart uploads at 20 MB. The endpoint also checks Length explicitly
// so the user gets a friendly 400 rather than an HTTP 413 from Kestrel.
const long MaxUploadBytes = 20L * 1024 * 1024;
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = MaxUploadBytes;
});
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = MaxUploadBytes;
});

builder.Services.AddSignalR();
builder.Services.AddSingleton<ImageStorage>();

// RabbitMQ
builder.Services
    .AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<RabbitMqOptions>>().Value);
builder.Services.AddSingleton<QueuePublisher>();

// Redis
builder.Services
    .AddOptions<RedisOptions>()
    .Bind(builder.Configuration.GetSection(RedisOptions.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<RedisOptions>>().Value);
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var opts = sp.GetRequiredService<RedisOptions>();
    return ConnectionMultiplexer.Connect(opts.ConnectionString);
});
builder.Services.AddSingleton<TaskStatusStore>();
builder.Services.AddSingleton<LifetimeStats>();

// Bridges Redis pub/sub -> SignalR.
builder.Services.AddHostedService<TaskStatusBroadcaster>();

// Tiny HTTP client to the RabbitMQ management API; used by /api/stats.
builder.Services.AddHttpClient<QueueStatsClient>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Serve the vanilla frontend from the configured folder. Same-origin with
// the API and SignalR hub, so no CORS dance.
var frontendConfigured = builder.Configuration["Frontend:Path"] ?? "../../frontend";
var frontendPath = Path.IsPathRooted(frontendConfigured)
    ? frontendConfigured
    : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, frontendConfigured));
if (Directory.Exists(frontendPath))
{
    var fileProvider = new PhysicalFileProvider(frontendPath);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
    app.UseStaticFiles(new StaticFileOptions  { FileProvider = fileProvider });
}

app.MapImagesEndpoints();
app.MapHub<TasksHub>("/hub/tasks");

// Lightweight stats endpoint for the worker-fleet UI.
app.MapGet("/api/stats", async (QueueStatsClient stats, CancellationToken ct)
    => Results.Ok(await stats.GetAsync(ct)));

// Lifetime counters: total tasks processed and bytes in vs bytes out.
app.MapGet("/api/lifetime-stats", async (LifetimeStats stats) =>
{
    var snapshot = await stats.GetAsync();
    return Results.Ok(snapshot);
});

app.Run();
