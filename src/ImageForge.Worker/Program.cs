using ImageForge.Shared.Messaging;
using ImageForge.Shared.Persistence;
using ImageForge.Worker.Services;
using ImageForge.Worker.Workers;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using StackExchange.Redis;

// Pin ImageSharp to one thread per worker process. By default it uses
// Environment.ProcessorCount parallelism for resize / encode, which means
// every replica fans out across all host cores. With N replicas on M cores
// you end up with N*M competing threads, context-switching kills throughput
// and starves the RabbitMQ heartbeat — the broker then thinks the worker is
// dead and re-queues the message, even though it had just finished encoding.
//
// One thread per worker keeps each replica honest about how much CPU it owns;
// pick scale = number of physical cores for best throughput.
SixLabors.ImageSharp.Configuration.Default.MaxDegreeOfParallelism = 1;

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
