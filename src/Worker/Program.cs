using Npgsql;
using RabbitMQ.Client;
using Shared;
using Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddObservability(serviceName: "demo-worker");

var pgConnString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Postgres connection string missing");
builder.Services.AddSingleton(new NpgsqlDataSourceBuilder(pgConnString).Build());

builder.Services.AddSingleton<IConnectionFactory>(_ => new ConnectionFactory
{
    HostName = builder.Configuration["RabbitMq:Host"] ?? "rabbitmq",
    UserName = builder.Configuration["RabbitMq:User"] ?? "guest",
    Password = builder.Configuration["RabbitMq:Password"] ?? "guest",
    DispatchConsumersAsync = true
});

builder.Services.AddHostedService<OrderConsumer>();

var host = builder.Build();
host.Run();
