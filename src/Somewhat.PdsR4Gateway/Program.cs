using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Somewhat.Auth;
using Somewhat.PdsR4Gateway.Configuration;
using Somewhat.PdsR4Gateway.Services;
using Somewhat.RateLimitedClient;
using Somewhat.SignedJwt;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<InboundAuthOptions>(builder.Configuration.GetSection(InboundAuthOptions.SectionName));
builder.Services.Configure<PdsApiOptions>(builder.Configuration.GetSection(PdsApiOptions.SectionName));
builder.Services.Configure<ApiQueueOptions>(builder.Configuration.GetSection("ApiQueueOptions"));
builder.Services.Configure<GatewayAuthOptions>(builder.Configuration.GetSection(GatewayAuthOptions.SectionName));

var inboundAuth = builder.Configuration.GetSection(InboundAuthOptions.SectionName).Get<InboundAuthOptions>()
	?? new InboundAuthOptions();
var pdsApi = builder.Configuration.GetSection(PdsApiOptions.SectionName).Get<PdsApiOptions>()
	?? new PdsApiOptions();
var gatewayAuth = builder.Configuration.GetSection(GatewayAuthOptions.SectionName).Get<GatewayAuthOptions>()
	?? new GatewayAuthOptions();

if (string.IsNullOrWhiteSpace(inboundAuth.JwtSigningKey))
{
	throw new InvalidOperationException($"Configuration '{InboundAuthOptions.SectionName}:JwtSigningKey' is required.");
}

if (string.IsNullOrWhiteSpace(pdsApi.BaseAddress) ||
	string.IsNullOrWhiteSpace(pdsApi.ApiKey))
{
	throw new InvalidOperationException(
		$"Configuration '{PdsApiOptions.SectionName}:BaseAddress' and '{PdsApiOptions.SectionName}:ApiKey' are required.");
}

builder.Services
	.AddAuthentication(options =>
	{
		options.DefaultScheme = GatewayAuthOptions.SmartScheme;
		options.DefaultAuthenticateScheme = GatewayAuthOptions.SmartScheme;
		options.DefaultChallengeScheme = gatewayAuth.EnableOidc
			? GatewayAuthOptions.OidcScheme
			: GatewayAuthOptions.CookieScheme;
	})
	.AddPolicyScheme(GatewayAuthOptions.SmartScheme, GatewayAuthOptions.SmartScheme, options =>
	{
		options.ForwardDefaultSelector = context =>
		{
			var authHeader = context.Request.Headers.Authorization.ToString();
			return authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
				? GatewayAuthOptions.BearerScheme
				: GatewayAuthOptions.CookieScheme;
		};
	})
	.AddCookie(GatewayAuthOptions.CookieScheme, options =>
	{
		options.Cookie.Name = gatewayAuth.CookieName;
		options.LoginPath = "/auth/login";
		options.LogoutPath = "/auth/logout";
	})
	.AddJwtBearer(GatewayAuthOptions.BearerScheme, options =>
	{
		if (!string.IsNullOrWhiteSpace(gatewayAuth.OidcAuthority))
		{
			options.Authority = gatewayAuth.OidcAuthority;
			options.RequireHttpsMetadata = gatewayAuth.RequireHttpsMetadata;
			options.TokenValidationParameters = new TokenValidationParameters
			{
				ValidateAudience = false,
				NameClaimType = "name",
				RoleClaimType = "role"
			};
			return;
		}

		options.TokenValidationParameters = new TokenValidationParameters
		{
			ValidateIssuer = true,
			ValidIssuer = inboundAuth.Issuer,
			ValidateAudience = true,
			ValidAudience = inboundAuth.Audience,
			ValidateIssuerSigningKey = true,
			IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(inboundAuth.JwtSigningKey)),
			ValidateLifetime = true,
			ClockSkew = TimeSpan.FromMinutes(1),
			NameClaimType = "name",
			RoleClaimType = "role"
		};
	});

if (gatewayAuth.EnableOidc)
{
	builder.Services.AddAuthentication()
		.AddOpenIdConnect(GatewayAuthOptions.OidcScheme, options =>
		{
			options.Authority = gatewayAuth.OidcAuthority;
			options.RequireHttpsMetadata = gatewayAuth.RequireHttpsMetadata;
			options.ClientId = gatewayAuth.OidcClientId;
			options.ResponseType = "code";
			options.UsePkce = true;
			options.SaveTokens = true;
			options.GetClaimsFromUserInfoEndpoint = true;
			options.Scope.Clear();
			options.Scope.Add("openid");
			options.Scope.Add("profile");
			options.Scope.Add(gatewayAuth.OidcScope);
			options.TokenValidationParameters = new TokenValidationParameters
			{
				NameClaimType = "name",
				RoleClaimType = "role"
			};
		});
}

builder.Services.AddAuthorization();

builder.Services.AddRoles(builder.Configuration);
builder.Services.AddSingleton<ActionMatcher>();

builder.Services.AddSignedJwtSupport(
	builder.Configuration.GetSection("SignedJwtClient"),
	builder.Configuration.GetSection("CertificateSource"));

builder.Services.PostConfigure<SigningCertificateRegistryOptions>(options =>
{
	foreach (var certificate in options.Certificates)
	{
		certificate.CertificatePath = ResolvePathFromContentRoot(certificate.CertificatePath);
		certificate.PrivateKeyPath = ResolvePathFromContentRoot(certificate.PrivateKeyPath);
		certificate.PfxPath = ResolvePathFromContentRoot(certificate.PfxPath);
	}
});

builder.Services.AddHttpClient(PdsApiOptions.HttpClientName, client =>
	{
		client.BaseAddress = new Uri(pdsApi.BaseAddress, UriKind.Absolute);
		client.DefaultRequestHeaders.Accept.Clear();
		client.DefaultRequestHeaders.Accept.ParseAdd("application/fhir+json");
	})
	.AddSignedJwtAuthentication(pdsApi.ApiKey, pdsApi.CertificateName);

builder.Services.AddSingleton<IRateLimitedApiClient>(serviceProvider =>
{
	var factory = serviceProvider.GetRequiredService<IHttpClientFactory>();
	var queueOptions = serviceProvider.GetRequiredService<IOptions<ApiQueueOptions>>().Value;
	return new RateLimitedApiClient(factory.CreateClient(PdsApiOptions.HttpClientName), queueOptions);
});

builder.Services.AddSingleton<IPdsR4Client, PdsR4Client>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<AuthorizationMiddleware>(new AuthorizationOptions { StrictMode = true });

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "Somewhat.PdsR4Gateway" })).AllowAnonymous();

app.MapGet("/auth/login", (HttpContext context, string? returnUrl) =>
{
	if (context.User.Identity?.IsAuthenticated == true)
	{
		return Results.LocalRedirect(returnUrl ?? "/");
	}

	if (!gatewayAuth.EnableOidc)
	{
		return Results.Problem("OIDC login is disabled for this environment.", statusCode: StatusCodes.Status400BadRequest);
	}

	var props = new AuthenticationProperties
	{
		RedirectUri = returnUrl ?? "/"
	};

	return Results.Challenge(props, [GatewayAuthOptions.OidcScheme]);
}).AllowAnonymous();

app.MapGet("/auth/logout", async (HttpContext context, string? returnUrl) =>
{
	await context.SignOutAsync(GatewayAuthOptions.CookieScheme);

	if (gatewayAuth.EnableOidc)
	{
		var props = new AuthenticationProperties
		{
			RedirectUri = returnUrl ?? "/"
		};
		return Results.SignOut(props, [GatewayAuthOptions.OidcScheme]);
	}

	return Results.LocalRedirect(returnUrl ?? "/");
}).AllowAnonymous();

app.MapGet("/me", (HttpContext context) =>
{
	var roles = context.User.Claims
		.Where(claim => claim.Type == System.Security.Claims.ClaimTypes.Role || claim.Type == "role")
		.Select(claim => claim.Value)
		.ToArray();

	return Results.Ok(new
	{
		user = context.User.Identity?.Name,
		roles
	}); 
}).AllowAuthenticatedUsers();

var pds = app.MapGroup("/pds").WithActionGroup("pds:patient");

pds.MapGet("/patient/{nhsNumber}", async (
	string nhsNumber,
	IPdsR4Client client,
	CancellationToken cancellationToken) =>
{
	try
	{
		var patient = await client.GetPatientByNhsNumberAsync(nhsNumber, RequestPriority.High, cancellationToken);
		return Results.Ok(patient);
	}
	catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
	{
		return Results.NotFound(new { error = ex.Message });
	}
	catch (HttpRequestException ex)
	{
		return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
	}
}).WithAction("read");

pds.MapGet("/patient", async (
	string? family,
	string? given,
	string? birthdate,
	IPdsR4Client client,
	CancellationToken cancellationToken) =>
{
	try
	{
		var bundle = await client.SearchPatientsAsync(family, given, birthdate, RequestPriority.Normal, cancellationToken);
		return Results.Ok(bundle);
	}
	catch (HttpRequestException ex)
	{
		return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
	}
}).WithAction("search");

pds.MapGet("/queue-depth", (IRateLimitedApiClient rateLimitedClient) =>
	Results.Ok(new { queueDepth = rateLimitedClient.QueueDepth }))
	.WithAction("monitor");

app.Run();

string? ResolvePathFromContentRoot(string? path)
{
	if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
	{
		return path;
	}

	return Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, path));
}

public partial class Program
{
}

public sealed class GatewayAuthOptions
{
	public const string SectionName = "GatewayAuth";

	public const string SmartScheme = "GatewaySmart";

	public const string CookieScheme = "GatewayCookie";

	public const string BearerScheme = "GatewayBearer";

	public const string OidcScheme = "GatewayOidc";

	public bool EnableOidc { get; set; }

	public string CookieName { get; set; } = "Somewhat.PdsGateway.Auth";

	public string OidcAuthority { get; set; } = string.Empty;

	public string OidcClientId { get; set; } = "gateway-client";

	public string OidcScope { get; set; } = "gateway.api";

	public bool RequireHttpsMetadata { get; set; }
}
