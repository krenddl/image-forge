using ImageForge.Shared.Messaging;
using ImageForge.Worker.Workers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName));

builder.Services.AddHostedService<QueueConsumer>();

var host = builder.Build();
host.Run();
