using ImageForge.Shared.Messaging;
using ImageForge.Shared.Persistence;
using ImageForge.Worker.Services;
using ImageForge.Worker.Workers;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

// RabbitMQ
builder.Services
    .AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName));

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

builder.Services.AddSingleton<WorkerStorage>();
builder.Services.AddSingleton<ImageProcessor>();
builder.Services.AddHostedService<QueueConsumer>();

var host = builder.Build();
host.Run();
