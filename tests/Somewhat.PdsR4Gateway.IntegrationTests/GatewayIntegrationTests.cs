using System.Net;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Somewhat.Auth;
using Somewhat.RateLimitedClient;

namespace Somewhat.PdsR4Gateway.IntegrationTests;

public sealed class GatewayIntegrationTests
{
    [Fact]
    public async Task PdsPatientEndpoint_ReturnsForbidden_WhenNoRolesResolved()
    {
        await using var factory = new GatewayWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/pds/patient/9000000009");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PdsPatientEndpoint_ReturnsMockPatient_WhenAuthorized()
    {
        await using var factory = new AuthorizedGatewayWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/pds/patient/9000000009");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("9000000009", payload);
        Assert.Contains("resourceType", payload);
    }

    [Fact]
    public async Task PdsSearchEndpoint_ReturnsBundle_WhenAuthorized()
    {
        await using var factory = new AuthorizedGatewayWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/pds/patient?family=SMITH&given=JOHN");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("Bundle", payload);
        Assert.Contains("SMITH", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PdsPatientEndpoint_ReturnsNotFound_ForUnknownPatient()
    {
        await using var factory = new AuthorizedGatewayWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/pds/patient/0000000000");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PdsPatientEndpoint_ReturnsBadGateway_WhenDownstreamRateLimited()
    {
        await using var factory = new ScenarioGatewayWebApplicationFactory(patientScenario: "rate-limited");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/pds/patient/9000000009");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("429", payload);
    }

    [Fact]
    public async Task PdsSearchEndpoint_ReturnsBadGateway_WhenDownstreamServerError()
    {
        await using var factory = new ScenarioGatewayWebApplicationFactory(searchScenario: "server-error");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/pds/patient?family=SMITH&given=JOHN");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("500", payload);
    }

    [Fact]
    public async Task PdsPatientEndpoint_ReturnsBadGateway_WhenDownstreamJwtMissing()
    {
        await using var factory = new MissingDownstreamJwtWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/pds/patient/9000000009");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("401", payload);
    }
}

internal class GatewayWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["InboundAuth:Issuer"] = "integration-test-issuer",
                ["InboundAuth:Audience"] = "integration-test-audience",
                ["InboundAuth:JwtSigningKey"] = "integration-tests-signing-key-32chars",
                ["PdsApi:BaseAddress"] = "https://mock-pds.local/",
                ["PdsApi:ApiKey"] = "test-api-key",
                ["PdsApi:CertificateName"] = "default",
                ["SignedJwtClient:Issuer"] = "integration-test-client",
                ["SignedJwtClient:Audience"] = "mock-pds",
                ["SignedJwtClient:Subject"] = "integration-tests",
                ["SignedJwtClient:ApiKeyClaimName"] = "client_id",
                ["SignedJwtClient:Scope"] = "pds.read",
                ["SignedJwtClient:TokenLifetime"] = "00:05:00",
                ["CertificateSource:DefaultCertificateName"] = "default",
                ["CertificateSource:Certificates:0:Name"] = "default",
                ["CertificateSource:Certificates:0:Source"] = "InlinePem",
                ["CertificateSource:Certificates:0:InlineCertificatePem"] = "unused-in-tests",
                ["CertificateSource:Certificates:0:InlinePrivateKeyPem"] = "unused-in-tests",
                ["ApiQueueOptions:MaxTransactionsPerSecond"] = "50",
                ["ApiQueueOptions:MaxConcurrentRequests"] = "2",
                ["ApiQueueOptions:MaxQueueDepth"] = "100",
                ["Integration:MockPds:IncludeBearerToken"] = "true"
            };

            config.AddInMemoryCollection(values);
        });

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IRateLimitedApiClient>(serviceProvider =>
            {
                var configuration = serviceProvider.GetRequiredService<IConfiguration>();
                var includeBearerToken = configuration.GetValue("Integration:MockPds:IncludeBearerToken", true);
                return new FakeRateLimitedApiClient(includeBearerToken);
            });
        });
    }
}

internal class AuthorizedGatewayWebApplicationFactory : GatewayWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IRoleService, AlwaysAllowRoleService>();
        });
    }
}

internal sealed class ScenarioGatewayWebApplicationFactory : AuthorizedGatewayWebApplicationFactory
{
    private readonly string? _patientScenario;
    private readonly string? _searchScenario;

    public ScenarioGatewayWebApplicationFactory(string? patientScenario = null, string? searchScenario = null)
    {
        _patientScenario = patientScenario;
        _searchScenario = searchScenario;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var overrides = new Dictionary<string, string?>();

            if (!string.IsNullOrWhiteSpace(_patientScenario))
            {
                overrides["PdsApi:PatientByNhsNumberRouteTemplate"] = $"Patient/{{nhsNumber}}?scenario={_patientScenario}";
            }

            if (!string.IsNullOrWhiteSpace(_searchScenario))
            {
                overrides["PdsApi:SearchRoute"] = $"Patient?scenario={_searchScenario}";
            }

            config.AddInMemoryCollection(overrides);
        });
    }
}

internal sealed class MissingDownstreamJwtWebApplicationFactory : AuthorizedGatewayWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Integration:MockPds:IncludeBearerToken"] = "false"
            });
        });
    }
}

internal sealed class AlwaysAllowRoleService : IRoleService
{
    private static readonly RoleDefinition[] Roles =
    [
        BuildRole()
    ];

    public Task<IEnumerable<RoleDefinition>> GetRolesAsync(System.Security.Claims.ClaimsPrincipal user)
    {
        return Task.FromResult<IEnumerable<RoleDefinition>>(Roles);
    }

    private static RoleDefinition BuildRole()
    {
        var role = new RoleDefinition
        {
            Name = "integration-role"
        };

        role.AllowedActions.Add("pds:patient:read");
        role.AllowedActions.Add("pds:patient:search");
        role.AllowedActions.Add("pds:patient:monitor");

        return role;
    }
}

internal sealed class FakeRateLimitedApiClient : IRateLimitedApiClient
{
    private readonly HttpClient _httpClient;

    public FakeRateLimitedApiClient(bool includeBearerToken)
    {
        _httpClient = new HttpClient(new MockPdsMessageHandler())
        {
            BaseAddress = new Uri("https://mock-pds.local/")
        };

        if (includeBearerToken)
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "integration-downstream-jwt");
        }
    }

    public int QueueDepth => 0;

    public Task<TResponse> EnqueueAsync<TResponse>(
        Func<HttpClient, CancellationToken, Task<TResponse>> requestFactory,
        RequestPriority priority = RequestPriority.Normal,
        CancellationToken cancellationToken = default)
    {
        return requestFactory(_httpClient, cancellationToken);
    }
}

internal sealed class MockPdsMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is null ||
            !string.Equals(request.Headers.Authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(request.Headers.Authorization.Parameter))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"issue\":[{\"diagnostics\":\"Missing bearer JWT\"}]}", Encoding.UTF8, "application/fhir+json")
            });
        }

        var path = request.RequestUri?.AbsolutePath?.Trim('/') ?? string.Empty;
        var query = request.RequestUri?.Query ?? string.Empty;

        if (request.Method == HttpMethod.Get && path.StartsWith("Patient/", StringComparison.OrdinalIgnoreCase))
        {
            var nhsNumber = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            var parsedQuery = QueryHelpers.ParseQuery(query);
            var scenario = parsedQuery.TryGetValue("scenario", out var scenarioValue)
                ? scenarioValue.ToString().ToLowerInvariant()
                : "ok";

            if (scenario == "rate-limited")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("{\"issue\":[{\"diagnostics\":\"Rate limit exceeded\"}]}", Encoding.UTF8, "application/fhir+json")
                });
            }

            if (scenario == "server-error")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"issue\":[{\"diagnostics\":\"Mock server error\"}]}", Encoding.UTF8, "application/fhir+json")
                });
            }

            if (nhsNumber == "0000000000")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{\"issue\":[{\"diagnostics\":\"Patient not found\"}]}", Encoding.UTF8, "application/fhir+json")
                });
            }

            var patientJson = JsonSerializer.Serialize(new
            {
                resourceType = "Patient",
                id = nhsNumber,
                identifier = new[]
                {
                    new { system = "https://fhir.nhs.uk/Id/nhs-number", value = nhsNumber }
                },
                name = new[]
                {
                    new { family = "SMITH", given = new[] { "JOHN" } }
                }
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(patientJson, Encoding.UTF8, "application/fhir+json")
            });
        }

        if (request.Method == HttpMethod.Get && path.Equals("Patient", StringComparison.OrdinalIgnoreCase))
        {
            var parsedQuery = QueryHelpers.ParseQuery(query);
            var family = parsedQuery.TryGetValue("family", out var familyValue) ? familyValue.ToString() : "SMITH";
            var given = parsedQuery.TryGetValue("given", out var givenValue) ? givenValue.ToString() : "JOHN";
            var birthdate = parsedQuery.TryGetValue("birthdate", out var birthdateValue) ? birthdateValue.ToString() : "1970-01-01";
            var scenario = parsedQuery.TryGetValue("scenario", out var scenarioValue)
                ? scenarioValue.ToString().ToLowerInvariant()
                : "ok";

            if (scenario == "rate-limited")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("{\"issue\":[{\"diagnostics\":\"Rate limit exceeded\"}]}", Encoding.UTF8, "application/fhir+json")
                });
            }

            if (scenario == "server-error")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"issue\":[{\"diagnostics\":\"Mock server error\"}]}", Encoding.UTF8, "application/fhir+json")
                });
            }

            var bundleJson = JsonSerializer.Serialize(new
            {
                resourceType = "Bundle",
                type = "searchset",
                total = 1,
                entry = new[]
                {
                    new
                    {
                        resource = new
                        {
                            resourceType = "Patient",
                            id = "9000000009",
                            name = new[] { new { family, given = new[] { given } } },
                            birthDate = birthdate
                        }
                    }
                }
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(bundleJson, Encoding.UTF8, "application/fhir+json")
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"issue\":[{\"diagnostics\":\"Unknown mock route\"}]}", Encoding.UTF8, "application/fhir+json")
        });
    }
}

