# Somewhat.PdsR4Gateway

A .NET 8 microservice wrapper for NHS Personal Demographics Service (PDS) FHIR R4 APIs.

This gateway composes three local libraries:

- `Somewhat.Auth`: endpoint-level authorization (`pds:patient:*` action model)
- `Somewhat.SignedJwt`: signed-JWT outbound authentication for NHS API calls
- `Somewhat.RateLimitedClient`: queueing and TPS/concurrency controls for outbound traffic

## Solution Layout

- `src/Somewhat.PdsR4Gateway`: production microservice
- `samples/Somewhat.PdsR4Gateway.Sample`: demo client that signs in via OIDC and calls gateway endpoints
- `samples/Somewhat.PdsR4Gateway.Sample.MockPdsApi`: mocked PDS R4 API with deterministic success and error scenarios
- `samples/Somewhat.PdsR4Gateway.Sample.MockOidcProvider`: mocked OIDC provider for local/compose demo authentication
- `tests/Somewhat.PdsR4Gateway.Tests`: test project
- `tests/Somewhat.PdsR4Gateway.IntegrationTests`: dedicated gateway integration tests using mocked downstream responses

## Endpoints

- `GET /health` (anonymous)
- `GET /auth/login?returnUrl=/` (OIDC login entrypoint)
- `GET /auth/logout?returnUrl=/` (logout)
- `GET /me` (any authenticated user)
- `GET /pds/patient/{nhsNumber}` (`pds:patient:read`)
- `GET /pds/patient?family=&given=&birthdate=` (`pds:patient:list`)
- `GET /pds/queue-depth` (`pds:patient:monitor`)

The demo UI at `/` includes role-switch login links for:

- `pds-reader-user` (role: `pds-reader`)
- `pds-operator-user` (roles: `pds-operator`)
- `no-roles-user` (no roles)

Use the UI buttons to call read/search/queue endpoints and compare responses by role.

## Configuration

Main configuration lives in `src/Somewhat.PdsR4Gateway/appsettings.json`.

Required values before running against NHS environments:

- `InboundAuth:JwtSigningKey`
- `PdsApi:ApiKey`
- `CertificateSource:Certificates[0]` certificate/key paths (or switch to cloud certificate source)

Certificate file paths are intentionally configured as relative defaults so they are portable across environments.
Set environment-specific values through configuration overrides, for example:

- `CertificateSource__Certificates__0__CertificatePath`
- `CertificateSource__Certificates__0__PrivateKeyPath`

Relevant sections:

- `InboundAuth`: inbound JWT bearer validation for callers of this microservice
- `GatewayAuth`: cookie auth + optional OIDC login config for interactive gateway sessions
- `Roles`: role-to-action mapping used by `Somewhat.Auth`
- `PdsApi`: NHS PDS base address and route templates
- `SignedJwtClient`: JWT claims/lifetime used for outbound signed token generation
- `CertificateSource`: signing certificate source for outbound JWT signing
- `ApiQueueOptions`: TPS, concurrency, and max queue depth

## Quick Start

From repository root:

```bash
dotnet restore
dotnet build
cd src/Somewhat.PdsR4Gateway
dotnet run
```

## Integration Tests

Run the dedicated integration test suite:

```bash
dotnet test tests/Somewhat.PdsR4Gateway.IntegrationTests/Somewhat.PdsR4Gateway.IntegrationTests.csproj
```

The integration tests run the gateway in-memory and inject a mocked rate-limited transport that simulates PDS responses for:

- successful patient read
- successful patient search bundle
- unknown patient (404)
- downstream rate-limit response (429 -> gateway 502)
- downstream server error (500 -> gateway 502)
- downstream JWT missing (401 -> gateway 502)
- authorization denied path

## Docker Compose Sample

Use the compose sample to run the mocked downstream API, gateway, and demo client together.

From repository root:

```bash
docker compose -f docker-compose.sample.yml up --build
```

Published ports:

- mock OIDC provider: `http://localhost:5180`
- mock PDS API: `http://localhost:5181`
- gateway: `http://localhost:5182`
- demo client: `http://localhost:5183`

Example demo calls:

```bash
curl -i "http://localhost:5183/auth/login?returnUrl=/"
curl -i "http://localhost:5183/demo/patient/9000000009"
curl -i "http://localhost:5183/demo/search?family=SMITH&given=JOHN"
```

The compose file uses development-only signing material in `samples/certs/`.
You can override those paths without editing files by setting:

- `PDS_SIGNING_CERT_PATH`
- `PDS_SIGNING_KEY_PATH`

## Local Startup Script

For non-docker local development, run all three projects with one command:

```bash
./scripts/start-local.sh
```

This starts services in dependency order using each project's `http` launch profile:

- mock OIDC provider on `http://localhost:5020`
- mock PDS API on `http://localhost:5252`
- gateway on `http://localhost:5298`
- demo client on `http://localhost:5072`

Logs are written to `.local-run/logs/`.

Stop all services:

```bash
./scripts/stop-local.sh
```

## Mocked Sample Flow

Terminal 1: run mocked downstream PDS API:

```bash
cd samples/Somewhat.PdsR4Gateway.Sample.MockOidcProvider
dotnet run
```

Default local URL (from launchSettings): `http://localhost:5020`

Terminal 2: run mocked downstream PDS API:

```bash
cd samples/Somewhat.PdsR4Gateway.Sample.MockPdsApi
dotnet run
```

Default local URL (from launchSettings): `http://localhost:5252`

Terminal 3: run the gateway and point `PdsApi:BaseAddress` to the mock API URL.

```bash
cd src/Somewhat.PdsR4Gateway
dotnet run
```

Default local URL (from launchSettings): `http://localhost:5298`

Terminal 4: run the demo client sample:

```bash
cd samples/Somewhat.PdsR4Gateway.Sample
dotnet run
```

Default local URL (from launchSettings): `http://localhost:5072`

Development appsettings are pre-aligned to these launchSettings ports, so local runs call:

- demo (`5072`) -> gateway (`5298`) -> mock-pds (`5252`)
- demo (`5072`) and gateway (`5298`) authenticate against mock-oidc (`5020`)

Demo client endpoints:

- `GET /auth/login?returnUrl=/`
- `GET /auth/logout?returnUrl=/`
- `GET /me`
- `GET /demo/patient/{nhsNumber}`
- `GET /demo/search?family=SMITH&given=JOHN&birthdate=1970-01-01`
- `GET /demo/queue-depth`

Mock API scenarios:

- all `/Patient` routes require `Authorization: Bearer <jwt>`
- `GET /Patient/{nhsNumber}?scenario=ok`
- `GET /Patient/{nhsNumber}?scenario=not-found`
- `GET /Patient/{nhsNumber}?scenario=rate-limited`
- `GET /Patient/{nhsNumber}?scenario=server-error`
- `GET /Patient?family=SMITH&given=JOHN&scenario=ok`

## Authorization Model

`Somewhat.Auth` is configured in strict mode. Every protected endpoint is decorated with both action group and action metadata:

- group: `pds:patient`
- actions: `read`, `list`, `monitor`

Callers must provide a valid inbound bearer JWT with role claims that map to those actions.
