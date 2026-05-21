using ImageForge.Api.Endpoints;
using ImageForge.Api.Services;
using ImageForge.Shared.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<ImageStorage>();

builder.Services
    .AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName));
builder.Services.AddSingleton<QueuePublisher>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/", () => "ImageForge API is running");
app.MapImagesEndpoints();

app.Run();
