using ImageForge.Api.Endpoints;
using ImageForge.Api.Hubs;
using ImageForge.Api.Services;
using ImageForge.Shared.Messaging;
using ImageForge.Shared.Persistence;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();
builder.Services.AddSingleton<ImageStorage>();

// RabbitMQ
builder.Services
    .AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
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

// Bridges Redis pub/sub -> SignalR.
builder.Services.AddHostedService<TaskStatusBroadcaster>();

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

app.Run();
