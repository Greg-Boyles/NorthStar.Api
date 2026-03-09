using NorthStar.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Add controllers
builder.Services.AddControllers();

// Add HttpClient factory
builder.Services.AddHttpClient();

// Register services
builder.Services.AddScoped<PolestarAuthService>();
builder.Services.AddScoped<PolestarCarService>();
builder.Services.AddScoped<PolestarTripService>();
builder.Services.AddScoped<PolestarStatusService>();
builder.Services.AddScoped<PolestarChargingScheduleService>();
builder.Services.AddScoped<PolestarClimateScheduleService>();

var app = builder.Build();

app.UseHttpsRedirection();

// Health check endpoint for ALB
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapControllers();

app.Run();
