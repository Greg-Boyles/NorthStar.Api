# NorthStar.Api

A .NET 8 Web API that provides a unified REST interface to Polestar car APIs. NorthStar proxies both GraphQL and gRPC endpoints to deliver car information, real-time vehicle telemetry, and remote control capabilities.

## Features

### Authentication
- **OIDC/PKCE Flow** — Secure authentication via Polestar ID (OAuth 2.0)

### Car Information
- **Car List** — Retrieve all cars associated with your account
- **Specifications** — Model details, battery specs, performance data
- **Software Version** — Current vehicle software version

### Real-Time Vehicle Data (via gRPC)
- **Battery Status** — Charge level, range, charging status, power/current/voltage
- **Trips & Odometer** — Trip meters (Auto/Manual/Since Charge), odometer, average speed/consumption
- **Vehicle Status** — Comprehensive snapshot including:
  - Exterior: locks, doors, windows, alarm
  - Availability: online status, usage mode
  - Climate: parking climatization, temperatures, seat/steering wheel heating
  - Health: service warnings, fluid levels

### Scheduling
- **Charging Schedule** — Global charge timer (start/stop times, activation, pending changes)
- **Climate Schedule** — Parking climatization timers and settings (temperature, seat heating, battery preconditioning)

## API Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/api/auth/login` | Authenticate with Polestar ID |
| `GET` | `/api/cars` | List all cars with telematics |
| `GET` | `/api/cars/{vin}/battery` | Battery and charging status |
| `GET` | `/api/cars/{vin}/trips` | Odometer and trip data |
| `GET` | `/api/cars/{vin}/status` | Comprehensive vehicle status |
| `GET` | `/api/cars/{vin}/charging-schedule` | Charge timer schedule |
| `GET` | `/api/cars/{vin}/climate-schedule` | Climate timer schedule |

## Getting Started

### Prerequisites
- .NET 8 SDK
- Valid Polestar ID credentials

### Running the API

```bash
dotnet run
```

The API will start at `https://localhost:7261`

### Example Usage

#### 1. Login
```bash
curl -X POST https://localhost:7261/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"your@email.com","password":"yourpassword"}'
```

Response includes `accessToken` — use it in subsequent requests.

#### 2. Get Cars
```bash
curl https://localhost:7261/api/cars \
  -H "Authorization: Bearer YOUR_ACCESS_TOKEN"
```

#### 3. Get Vehicle Status
```bash
curl https://localhost:7261/api/cars/YOUR_VIN/status \
  -H "Authorization: Bearer YOUR_ACCESS_TOKEN"
```

### Postman Collection

Import `NorthStar.postman_collection.json` for a complete collection with:
- Auto-token storage after login
- Auto-VIN extraction from car list
- All endpoints pre-configured

## Architecture

### Backend Services
- **Polestar GraphQL** — Car metadata and telematics (`pc-api.polestar.com`)
- **C3 gRPC** — Real-time vehicle state from Volvo's digital twin platform (`cepmobtoken.eu.prod.c3.volvocars.com`)
- **PCCS gRPC** — Scheduling services (Chronos v1/v2 on `api.pccs-prod.plstr.io`)

### Technology Stack
- **ASP.NET Core 8** — Web API framework
- **Grpc.Net.Client** — gRPC client for vehicle services
- **Protobuf** — Protocol buffer definitions for gRPC services
- **System.Text.Json** — GraphQL query handling

### Proto Definitions
Proto files define gRPC contracts:
- `odometer.proto` — Trip and odometer service
- `battery.proto` — Battery and charging service
- `exterior.proto` — Locks, doors, windows
- `availability.proto` — Vehicle availability/online status
- `parkingclimatization.proto` — Climate control service
- `chronos.proto` — Global charge timer (Chronos v2)
- `parkingclimatetimer.proto` — Climate schedule (Chronos v1)

## Project Structure

```
NorthStar.Api/
├── Controllers/
│   ├── AuthController.cs          # OIDC authentication
│   └── CarsController.cs           # All car endpoints
├── Models/
│   ├── Car.cs                      # Car and telematics models
│   ├── TripData.cs                 # Trip/odometer models
│   ├── VehicleStatus.cs            # Comprehensive status models
│   ├── ChargingSchedule.cs         # Charge timer models
│   └── ClimateSchedule.cs          # Climate timer models
├── Services/
│   ├── PolestarAuthService.cs      # OIDC/PKCE flow
│   ├── PolestarCarService.cs       # GraphQL queries
│   ├── PolestarTripService.cs      # Odometer + battery gRPC
│   ├── PolestarStatusService.cs    # Multi-service status aggregation
│   ├── PolestarChargingScheduleService.cs  # Charge timer gRPC
│   └── PolestarClimateScheduleService.cs   # Climate timer gRPC
└── Protos/                         # gRPC proto definitions
```

## Notes

- **Token Expiry**: Access tokens expire after ~2 hours. Re-authenticate when receiving 401 errors.
- **Timeouts**: gRPC calls have 15-20 second timeouts. If the car is asleep, calls may time out (504 response).
- **Rate Limits**: No documented limits, but avoid excessive polling. Consider caching responses.
- **VIN Format**: All VINs are 17-character alphanumeric codes (e.g., `YSMVSEUU8SL310560`).

## License

This is an unofficial API proxy. Polestar and Volvo trademarks belong to their respective owners.
