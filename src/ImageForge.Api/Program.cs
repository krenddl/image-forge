using ImageForge.Api.Endpoints;
using ImageForge.Api.Hubs;
using ImageForge.Api.Services;
using ImageForge.Shared.Messaging;
using ImageForge.Shared.Persistence;
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

app.MapGet("/", () => "ImageForge API is running");
app.MapImagesEndpoints();
app.MapHub<TasksHub>("/hub/tasks");

app.Run();
