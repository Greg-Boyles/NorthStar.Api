using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NorthStar.Api.Interfaces;
using NorthStar.Api.Models;

namespace NorthStar.Api.Services;

public class PolestarCarService : ICarService
{
    private const string ApiUrl = "https://pc-api.polestar.com/eu-north-1/mystar-v2/";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PolestarCarService> _logger;

    public PolestarCarService(IHttpClientFactory httpClientFactory, ILogger<PolestarCarService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<List<Car>> GetCarsAsync(string accessToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Query cars
        _logger.LogInformation("Fetching cars via GraphQL");
        var carsResult = await GraphqlQueryAsync(client, CarsQuery, new { });
        var cars = ParseCars(carsResult);

        if (cars.Count == 0)
            return cars;

        // Query telematics for all VINs
        var vins = cars.Where(c => c.Vin != null).Select(c => c.Vin!).ToList();
        if (vins.Count > 0)
        {
            _logger.LogInformation("Fetching telematics for {Count} VINs", vins.Count);
            var telResult = await GraphqlQueryAsync(client, TelematicsQuery, new { vins });
            EnrichCarsWithTelematics(cars, telResult);
        }

        return cars;
    }

    private async Task<JsonDocument> GraphqlQueryAsync(HttpClient client, string query, object variables)
    {
        var payload = JsonSerializer.Serialize(new { query, variables });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var resp = await client.PostAsync(ApiUrl, content);
        resp.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
    }

    private static List<Car> ParseCars(JsonDocument doc)
    {
        var cars = new List<Car>();
        if (!doc.RootElement.TryGetProperty("data", out var data))
            return cars;
        if (!data.TryGetProperty("getConsumerCarsV2", out var carsArray))
            return cars;

        foreach (var c in carsArray.EnumerateArray())
        {
            var car = new Car
            {
                Vin = c.GetProp("vin"),
                RegistrationNo = c.GetProp("registrationNo"),
                ModelYear = c.GetProp("modelYear"),
                Edition = c.GetProp("edition"),
                Market = c.GetProp("market"),
                FuelType = c.GetProp("fuelType"),
                Drivetrain = c.GetProp("drivetrain"),
                NumberOfDoors = c.GetIntProp("numberOfDoors"),
                NumberOfSeats = c.GetIntProp("numberOfSeats"),
                FactoryCompleteDate = c.GetProp("factoryCompleteDate"),
                RegistrationDate = c.GetProp("registrationDate"),
                DeliveryDate = c.GetProp("deliveryDate"),
                SoftwareVersion = c.TryGetProperty("software", out var sw) ? sw.GetProp("version") : null
            };

            if (c.TryGetProperty("content", out var content))
            {
                if (content.TryGetProperty("model", out var model))
                    car.ModelName = model.GetProp("name");

                if (content.TryGetProperty("specification", out var spec))
                {
                    car.Specification = new CarSpecification
                    {
                        Battery = spec.GetProp("battery"),
                        ElectricMotors = spec.GetProp("electricMotors"),
                        Performance = spec.GetProp("performance"),
                        Torque = spec.GetProp("torque"),
                        TotalHp = spec.GetProp("totalHp"),
                        TotalKw = spec.GetProp("totalKw"),
                        BodyType = spec.GetProp("bodyType")
                    };
                }
            }

            cars.Add(car);
        }

        return cars;
    }

    private static void EnrichCarsWithTelematics(List<Car> cars, JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("data", out var data))
            return;
        if (!data.TryGetProperty("carTelematicsV2", out var tel))
            return;

        // Battery
        if (tel.TryGetProperty("battery", out var batteries))
        {
            foreach (var b in batteries.EnumerateArray())
            {
                var vin = b.GetProp("vin");
                var car = cars.FirstOrDefault(c => c.Vin == vin);
                if (car == null) continue;
                car.Battery = new CarBattery
                {
                    ChargeLevelPercentage = b.GetIntProp("batteryChargeLevelPercentage"),
                    ChargingStatus = b.GetProp("chargingStatus"),
                    EstimatedChargingTimeMinutes = b.GetIntProp("estimatedChargingTimeToFullMinutes"),
                    EstimatedRangeKm = b.GetIntProp("estimatedDistanceToEmptyKm"),
                    Timestamp = ParseTimestamp(b)
                };
            }
        }

        // Odometer
        if (tel.TryGetProperty("odometer", out var odometers))
        {
            foreach (var o in odometers.EnumerateArray())
            {
                var vin = o.GetProp("vin");
                var car = cars.FirstOrDefault(c => c.Vin == vin);
                if (car == null) continue;
                var meters = o.GetIntProp("odometerMeters") ?? 0;
                car.Odometer = new CarOdometer
                {
                    OdometerMeters = meters,
                    OdometerKm = Math.Round(meters / 1000.0, 1),
                    OdometerMiles = Math.Round(meters / 1609.344, 1),
                    Timestamp = ParseTimestamp(o)
                };
            }
        }
    }

    private static DateTime? ParseTimestamp(JsonElement el)
    {
        if (!el.TryGetProperty("timestamp", out var ts)) return null;
        if (!ts.TryGetProperty("seconds", out var secEl)) return null;
        var seconds = secEl.ValueKind == JsonValueKind.Number
            ? secEl.GetInt64()
            : long.TryParse(secEl.GetString(), out var s) ? s : 0;
        return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : null;
    }

    private const string CarsQuery = @"
        query GetConsumerCarsV2 {
            getConsumerCarsV2 {
                vin
                registrationNo
                modelYear
                market
                edition
                fuelType
                drivetrain
                numberOfDoors
                numberOfSeats
                factoryCompleteDate
                registrationDate
                deliveryDate
                content {
                    model { name }
                    specification {
                        battery
                        electricMotors
                        performance
                        torque
                        totalHp
                        totalKw
                        bodyType
                    }
                }
                software { version }
            }
        }";

    private const string TelematicsQuery = @"
        query CarTelematicsV2($vins: [String!]!) {
            carTelematicsV2(vins: $vins) {
                battery {
                    vin
                    batteryChargeLevelPercentage
                    chargingStatus
                    estimatedChargingTimeToFullMinutes
                    estimatedDistanceToEmptyKm
                    timestamp { seconds nanos }
                }
                odometer {
                    vin
                    odometerMeters
                    timestamp { seconds nanos }
                }
            }
        }";
}

// Extension methods for safe JSON property access
internal static class JsonElementExtensions
{
    public static string? GetProp(this JsonElement el, string name)
    {
        return el.TryGetProperty(name, out var prop) && prop.ValueKind != JsonValueKind.Null
            ? prop.ToString()
            : null;
    }

    public static int? GetIntProp(this JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;
        if (prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt32();
        return int.TryParse(prop.GetString(), out var v) ? v : null;
    }
}
