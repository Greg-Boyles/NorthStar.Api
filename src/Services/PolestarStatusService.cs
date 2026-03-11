using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using NorthStar.Api.Interfaces;
using NorthStar.Api.Models;
using ExteriorProtos = NorthStar.Api.Protos.Exterior;
using AvailabilityProtos = NorthStar.Api.Protos.Availability;
using ClimateProtos = NorthStar.Api.Protos.ParkingClimatization;
using BatteryProtos = NorthStar.Api.Protos.Battery;

namespace NorthStar.Api.Services;

public class PolestarStatusService : IStatusService
{
    private const string C3Host = "https://cepmobtoken.eu.prod.c3.volvocars.com";
    private const string GraphqlUrl = "https://pc-api.polestar.com/eu-north-1/mystar-v2/";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PolestarStatusService> _logger;

    public PolestarStatusService(IHttpClientFactory httpClientFactory, ILogger<PolestarStatusService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<VehicleStatus> GetVehicleStatusAsync(string accessToken, string vin, CancellationToken ct = default)
    {
        _logger.LogInformation("Querying comprehensive vehicle status for VIN: {Vin}", vin);

        using var channel = GrpcChannel.ForAddress(C3Host);
        var headers = new Metadata
        {
            { "authorization", $"Bearer {accessToken}" },
            { "vin", vin }
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        // Query all services in parallel
        var exteriorTask = GetExteriorAsync(channel, headers, vin, cts.Token);
        var availabilityTask = GetAvailabilityAsync(channel, headers, vin, cts.Token);
        var climateTask = GetClimateAsync(channel, headers, vin, cts.Token);
        var batteryTask = GetBatteryAsync(channel, headers, vin, cts.Token);
        var healthTask = GetHealthAsync(accessToken, vin, cts.Token);

        // Await all — catching individual failures so partial data is still returned
        var exterior = await SafeAwait(exteriorTask, "Exterior");
        var availability = await SafeAwait(availabilityTask, "Availability");
        var climate = await SafeAwait(climateTask, "Climate");
        var battery = await SafeAwait(batteryTask, "Battery");
        var health = await SafeAwait(healthTask, "Health");

        return new VehicleStatus
        {
            Vin = vin,
            Timestamp = DateTime.UtcNow,
            Exterior = exterior,
            Availability = availability,
            Climate = climate,
            Battery = battery,
            Health = health
        };
    }

    private async Task<T?> SafeAwait<T>(Task<T?> task, string name) where T : class
    {
        try
        {
            return await task;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to fetch {Name} data: {Message}", name, ex.Message);
            return null;
        }
    }

    // --- Exterior ---

    public async Task<ExteriorStatus?> GetExteriorAsync(GrpcChannel channel, Metadata headers, string vin, CancellationToken ct)
    {
        var client = new ExteriorProtos.ExteriorService.ExteriorServiceClient(channel);
        var request = new ExteriorProtos.GetExteriorRequest { Id = Guid.NewGuid().ToString(), Vin = vin };
        var response = await client.GetLatestExteriorAsync(request, headers, cancellationToken: ct);
        var ext = response.Exterior;
        if (ext == null) return null;

        return ProtoMappers.MapExterior(ext);
    }

    // --- Availability ---

    public async Task<AvailabilityInfo?> GetAvailabilityAsync(GrpcChannel channel, Metadata headers, string vin, CancellationToken ct)
    {
        var client = new AvailabilityProtos.AvailabilityService.AvailabilityServiceClient(channel);
        var request = new AvailabilityProtos.GetAvailabilityRequest { Id = Guid.NewGuid().ToString(), Vin = vin };
        var response = await client.GetLatestAvailabilityAsync(request, headers, cancellationToken: ct);
        var avail = response.Availability;
        if (avail == null) return null;

        return ProtoMappers.MapAvailability(avail);
    }

    // --- Climate ---

    public async Task<ClimateStatus?> GetClimateAsync(GrpcChannel channel, Metadata headers, string vin, CancellationToken ct)
    {
        var client = new ClimateProtos.ParkingClimatizationService.ParkingClimatizationServiceClient(channel);
        var request = new ClimateProtos.GetParkingClimatizationRequest { Id = Guid.NewGuid().ToString(), Vin = vin };
        var response = await client.GetLatestParkingClimatizationAsync(request, headers, cancellationToken: ct);
        var pc = response.ParkingClimatization;
        if (pc == null) return null;

        return ProtoMappers.MapClimate(pc);
    }

    // --- Battery ---

    public async Task<BatteryStatus?> GetBatteryAsync(GrpcChannel channel, Metadata headers, string vin, CancellationToken ct)
    {
        var client = new BatteryProtos.BatteryService.BatteryServiceClient(channel);
        var request = new BatteryProtos.GetBatteryRequest { Id = Guid.NewGuid().ToString(), Vin = vin };
        var response = await client.GetLatestBatteryAsync(request, headers, cancellationToken: ct);
        var b = response.Battery;
        if (b == null) return null;

        return ProtoMappers.MapBatteryStatus(b);
    }

    // --- Health (GraphQL) ---

    public async Task<HealthStatus?> GetHealthAsync(string accessToken, string vin, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var payload = JsonSerializer.Serialize(new
        {
            query = HealthQuery,
            variables = new { vins = new[] { vin } }
        });

        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var resp = await client.PostAsync(GraphqlUrl, content, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
        if (!data.TryGetProperty("carTelematicsV2", out var tel)) return null;
        if (!tel.TryGetProperty("health", out var healthArray)) return null;

        foreach (var h in healthArray.EnumerateArray())
        {
            if (h.GetProp("vin") != vin) continue;

            var distKm = h.GetIntProp("distanceToServiceKm");
            return new HealthStatus
            {
                Timestamp = ParseGraphqlTimestamp(h),
                ServiceWarning = h.GetProp("serviceWarning"),
                DaysToService = h.GetIntProp("daysToService"),
                DistanceToServiceKm = distKm,
                DistanceToServiceMiles = distKm.HasValue ? (int)Math.Round(distKm.Value / 1.60934) : null,
                BrakeFluidLevelWarning = h.GetProp("brakeFluidLevelWarning"),
                EngineCoolantLevelWarning = h.GetProp("engineCoolantLevelWarning"),
                OilLevelWarning = h.GetProp("oilLevelWarning"),
                WasherFluidLevelWarning = h.GetProp("washerFluidLevelWarning")
            };
        }

        return null;
    }

    /// <summary>Map raw battery proto to BatteryStatus model (for status section).</summary>
    public BatteryStatus? MapBatteryStatus(BatteryProtos.Battery? b)
    {
        if (b == null) return null;
        return ProtoMappers.MapBatteryStatus(b);
    }

    // --- Formatting helpers ---

    private static DateTime? ParseGraphqlTimestamp(JsonElement el)
    {
        if (!el.TryGetProperty("timestamp", out var ts)) return null;
        if (!ts.TryGetProperty("seconds", out var secEl)) return null;
        var seconds = secEl.ValueKind == JsonValueKind.Number
            ? secEl.GetInt64()
            : long.TryParse(secEl.GetString(), out var s) ? s : 0;
        return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : null;
    }

    private const string HealthQuery = @"
        query CarTelematicsV2($vins: [String!]!) {
            carTelematicsV2(vins: $vins) {
                health {
                    vin
                    serviceWarning
                    daysToService
                    distanceToServiceKm
                    brakeFluidLevelWarning
                    engineCoolantLevelWarning
                    oilLevelWarning
                    washerFluidLevelWarning
                    timestamp { seconds nanos }
                }
            }
        }";
}
