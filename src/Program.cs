using NorthStar.Api.Services;
using Serilog;
using Testcontainers.Redis;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting", Serilog.Events.LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("Starting NorthStar API");

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    // Add controllers
    builder.Services.AddControllers();

    // Add health checks
    builder.Services.AddHealthChecks();

    // Add HttpClient factory
    builder.Services.AddHttpClient();

    // Start Redis container in development environment
    RedisContainer? redisContainer = null;
    if (builder.Environment.IsDevelopment())
    {
        Log.Information("Development environment detected - starting Redis container via Testcontainers");
        redisContainer = new RedisBuilder("redis:7-alpine")
            .Build();
        
        await redisContainer.StartAsync();
        Log.Information("Redis container started at {Endpoint}", redisContainer.GetConnectionString());
        
        // Register container for cleanup on shutdown
        builder.Services.AddSingleton(redisContainer);
    }

    // Add Redis distributed cache
    var redisEndpoint = builder.Environment.IsDevelopment() && redisContainer != null
        ? redisContainer.GetConnectionString()
        : builder.Configuration.GetValue<string>("REDIS_ENDPOINT") ?? "localhost:6379";
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
        StackExchange.Redis.ConnectionMultiplexer.Connect(redisEndpoint));
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisEndpoint;
        options.InstanceName = "northstar:";
    });

    // Register services
    builder.Services.AddSingleton<RedisLockService>();
    builder.Services.AddSingleton<VehicleStateCache>();
    builder.Services.AddHostedService<VehicleStreamService>();
    builder.Services.AddScoped<PolestarAuthService>();
    builder.Services.AddScoped<PolestarCarService>();
    builder.Services.AddScoped<PolestarTripService>();
    builder.Services.AddScoped<PolestarStatusService>();
    builder.Services.AddScoped<PolestarChargingScheduleService>();
    builder.Services.AddScoped<PolestarClimateScheduleService>();
    builder.Services.AddScoped<VehicleSnapshotService>();

    var app = builder.Build();

    // Request logging middleware
    app.UseSerilogRequestLogging();

    app.UseHttpsRedirection();

    // Health check endpoint
    app.MapHealthChecks("/health");

    app.MapControllers();

    // Register cleanup for Redis container on shutdown
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStopping.Register(() =>
    {
        if (redisContainer != null)
        {
            Log.Information("Stopping Redis container");
            redisContainer.StopAsync().GetAwaiter().GetResult();
            redisContainer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    });

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
