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
app.MapControllers();

app.Run();
