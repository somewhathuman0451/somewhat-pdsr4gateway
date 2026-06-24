var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
	service = "Somewhat.PdsR4Gateway.Sample.MockPdsApi",
	description = "Mocked NHS PDS R4 API",
	scenarios = new[] { "ok", "not-found", "rate-limited", "server-error" },
	requiresBearerJwt = true
}));

app.MapGet("/Patient/{nhsNumber}", (HttpRequest request, string nhsNumber, string? scenario) =>
{
	if (!HasBearerToken(request))
	{
		return Results.Unauthorized();
	}

	var effectiveScenario = (scenario ?? "ok").ToLowerInvariant();

	if (effectiveScenario == "not-found" || nhsNumber == "0000000000")
	{
		return Results.NotFound(new
		{
			resourceType = "OperationOutcome",
			issue = new[]
			{
				new { severity = "error", code = "not-found", diagnostics = "Mock patient not found." }
			}
		});
	}

	if (effectiveScenario == "rate-limited")
	{
		return Results.StatusCode(StatusCodes.Status429TooManyRequests);
	}

	if (effectiveScenario == "server-error")
	{
		return Results.StatusCode(StatusCodes.Status500InternalServerError);
	}

	return Results.Ok(new
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
		},
		birthDate = "1970-01-01"
	});
});

app.MapGet("/Patient", (HttpRequest request) =>
{
	if (!HasBearerToken(request))
	{
		return Results.Unauthorized();
	}

	var query = request.Query;
	var family = query.TryGetValue("family", out var familyValue) ? familyValue.ToString() : "SMITH";
	var given = query.TryGetValue("given", out var givenValue) ? givenValue.ToString() : "JOHN";
	var birthdate = query.TryGetValue("birthdate", out var birthdateValue) ? birthdateValue.ToString() : "1970-01-01";
	var scenario = query.TryGetValue("scenario", out var scenarioValue) ? scenarioValue.ToString().ToLowerInvariant() : "ok";

	if (scenario == "server-error")
	{
		return Results.StatusCode(StatusCodes.Status500InternalServerError);
	}

	return Results.Ok(new
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
					name = new[]
					{
						new { family, given = new[] { given } }
					},
					birthDate = birthdate
				}
			}
		}
	});
});

app.Run();

static bool HasBearerToken(HttpRequest request)
{
	var header = request.Headers.Authorization.ToString();
	return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
		&& !string.IsNullOrWhiteSpace(header["Bearer ".Length..]);
}
