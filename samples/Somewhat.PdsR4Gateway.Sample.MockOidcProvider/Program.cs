using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MockOidcOptions>(builder.Configuration.GetSection(MockOidcOptions.SectionName));

var oidcOptions = builder.Configuration.GetSection(MockOidcOptions.SectionName).Get<MockOidcOptions>()
	?? new MockOidcOptions();

var issuer = oidcOptions.Issuer.TrimEnd('/');
var certificatePath = ResolvePathFromContentRoot(builder.Environment.ContentRootPath, oidcOptions.SigningCertificatePath);
var privateKeyPath = ResolvePathFromContentRoot(builder.Environment.ContentRootPath, oidcOptions.SigningPrivateKeyPath);

var signingCertificate = X509Certificate2.CreateFromPemFile(certificatePath, privateKeyPath);
var signingKey = new X509SecurityKey(signingCertificate)
{
	KeyId = signingCertificate.Thumbprint
};
var signingCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);

var jwk = JsonWebKeyConverter.ConvertFromX509SecurityKey(signingKey);
jwk.Kid = signingKey.KeyId;

var validClients = oidcOptions.Clients
	.Where(client => !string.IsNullOrWhiteSpace(client.ClientId))
	.GroupBy(client => client.ClientId, StringComparer.Ordinal)
	.Select(group => group.Last())
	.ToDictionary(client => client.ClientId, StringComparer.Ordinal);

var validUsers = oidcOptions.Users
	.Where(user => !string.IsNullOrWhiteSpace(user.Username))
	.GroupBy(user => user.Username, StringComparer.OrdinalIgnoreCase)
	.Select(group => group.Last())
	.ToDictionary(user => user.Username, StringComparer.OrdinalIgnoreCase);

var authorizationCodes = new ConcurrentDictionary<string, AuthorizationCodeEntry>(StringComparer.Ordinal);

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
	service = "Somewhat.PdsR4Gateway.Sample.MockOidcProvider",
	issuer,
	users = validUsers.Values.Select(user => new { user.Username, user.DisplayName, user.Roles }),
	endpoints = new[]
	{
		"/.well-known/openid-configuration",
		"/.well-known/jwks.json",
		"/authorize",
		"/token",
		"/userinfo",
		"/endsession"
	}
}));

app.MapGet("/.well-known/openid-configuration", () => Results.Json(new
{
	issuer,
	authorization_endpoint = $"{issuer}/authorize",
	token_endpoint = $"{issuer}/token",
	userinfo_endpoint = $"{issuer}/userinfo",
	jwks_uri = $"{issuer}/.well-known/jwks.json",
	end_session_endpoint = $"{issuer}/endsession",
	response_types_supported = new[] { "code" },
	subject_types_supported = new[] { "public" },
	id_token_signing_alg_values_supported = new[] { SecurityAlgorithms.RsaSha256 },
	scopes_supported = new[] { "openid", "profile", oidcOptions.ApiScope },
	token_endpoint_auth_methods_supported = new[] { "none" },
	claims_supported = new[] { "sub", "name", "role" }
}));

app.MapGet("/.well-known/jwks.json", () => Results.Json(new { keys = new[] { jwk } }));

app.MapGet("/authorize", (HttpRequest request) =>
{
	var clientId = request.Query["client_id"].ToString();
	var redirectUri = request.Query["redirect_uri"].ToString();
	var responseType = request.Query["response_type"].ToString();
	var scope = request.Query["scope"].ToString();
	var state = request.Query["state"].ToString();
	var nonce = request.Query["nonce"].ToString();
	var loginHint = request.Query["login_hint"].ToString();

	if (!string.Equals(responseType, "code", StringComparison.Ordinal))
	{
		return Results.BadRequest(new { error = "unsupported_response_type" });
	}

	if (!validClients.TryGetValue(clientId, out var client) ||
		!client.RedirectUris.Contains(redirectUri, StringComparer.OrdinalIgnoreCase))
	{
		return Results.BadRequest(new { error = "invalid_client" });
	}

	var selectedUserName = string.IsNullOrWhiteSpace(loginHint)
		? oidcOptions.DefaultUsername
		: loginHint;

	if (string.IsNullOrWhiteSpace(selectedUserName) ||
		!validUsers.TryGetValue(selectedUserName, out var selectedUser))
	{
		return Results.BadRequest(new { error = "invalid_request", error_description = "Unknown user/login_hint." });
	}

	var code = Guid.NewGuid().ToString("N");
	authorizationCodes[code] = new AuthorizationCodeEntry(
		ClientId: clientId,
		RedirectUri: redirectUri,
		Scope: scope,
		Nonce: nonce,
		Subject: selectedUser.Subject,
		DisplayName: selectedUser.DisplayName,
		Username: selectedUser.Username,
		Roles: selectedUser.Roles,
		ExpiresUtc: DateTimeOffset.UtcNow.AddMinutes(2));

	var location = redirectUri + "?code=" + Uri.EscapeDataString(code);
	if (!string.IsNullOrWhiteSpace(state))
	{
		location += "&state=" + Uri.EscapeDataString(state);
	}

	return Results.Redirect(location);
});

app.MapPost("/token", async (HttpRequest request) =>
{
	var form = await request.ReadFormAsync();
	var grantType = form["grant_type"].ToString();
	var code = form["code"].ToString();
	var clientId = form["client_id"].ToString();
	var redirectUri = form["redirect_uri"].ToString();

	if (!string.Equals(grantType, "authorization_code", StringComparison.Ordinal))
	{
		return Results.BadRequest(new { error = "unsupported_grant_type" });
	}

	if (!authorizationCodes.TryRemove(code, out var codeEntry))
	{
		return Results.BadRequest(new { error = "invalid_grant" });
	}

	if (codeEntry.ExpiresUtc < DateTimeOffset.UtcNow ||
		!string.Equals(codeEntry.ClientId, clientId, StringComparison.Ordinal) ||
		!string.Equals(codeEntry.RedirectUri, redirectUri, StringComparison.OrdinalIgnoreCase))
	{
		return Results.BadRequest(new { error = "invalid_grant" });
	}

	var now = DateTime.UtcNow;
	var nowEpoch = new DateTimeOffset(now).ToUnixTimeSeconds().ToString();

	var accessTokenClaims = new List<Claim>
	{
		new(JwtRegisteredClaimNames.Sub, codeEntry.Subject),
		new("name", codeEntry.DisplayName),
		new("preferred_username", codeEntry.Username),
		new(JwtRegisteredClaimNames.Iat, nowEpoch, ClaimValueTypes.Integer64)
	};
	accessTokenClaims.AddRange(codeEntry.Roles.Select(role => new Claim("role", role)));

	var accessToken = new JwtSecurityToken(
		issuer: issuer,
		audience: oidcOptions.AccessTokenAudience,
		claims: accessTokenClaims,
		notBefore: now,
		expires: now.AddMinutes(20),
		signingCredentials: signingCredentials);

	var idTokenClaims = new List<Claim>
	{
		new(JwtRegisteredClaimNames.Sub, codeEntry.Subject),
		new("name", codeEntry.DisplayName),
		new("preferred_username", codeEntry.Username),
		new(JwtRegisteredClaimNames.Iat, nowEpoch, ClaimValueTypes.Integer64),
		new("auth_time", nowEpoch, ClaimValueTypes.Integer64)
	};
	idTokenClaims.AddRange(codeEntry.Roles.Select(role => new Claim("role", role)));

	if (!string.IsNullOrWhiteSpace(codeEntry.Nonce))
	{
		idTokenClaims.Add(new Claim(JwtRegisteredClaimNames.Nonce, codeEntry.Nonce));
	}

	var idToken = new JwtSecurityToken(
		issuer: issuer,
		audience: codeEntry.ClientId,
		claims: idTokenClaims,
		notBefore: now,
		expires: now.AddMinutes(20),
		signingCredentials: signingCredentials);

	var tokenHandler = new JwtSecurityTokenHandler();

	return Results.Json(new
	{
		token_type = "Bearer",
		expires_in = 1200,
		scope = codeEntry.Scope,
		access_token = tokenHandler.WriteToken(accessToken),
		id_token = tokenHandler.WriteToken(idToken)
	});
});

app.MapGet("/userinfo", (HttpRequest request) =>
{
	var bearer = request.Headers.Authorization.ToString();
	if (!bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
	{
		return Results.Unauthorized();
	}

	var token = bearer["Bearer ".Length..].Trim();
	if (string.IsNullOrWhiteSpace(token))
	{
		return Results.Unauthorized();
	}

	try
	{
		var tokenHandler = new JwtSecurityTokenHandler();
		var rawToken = tokenHandler.ReadJwtToken(token);
		var subject = rawToken.Claims.FirstOrDefault(claim => claim.Type == JwtRegisteredClaimNames.Sub)?.Value;

		if (string.IsNullOrWhiteSpace(subject))
		{
			return Results.Unauthorized();
		}

		var validationParameters = new TokenValidationParameters
		{
			ValidateIssuer = true,
			ValidIssuer = issuer,
			ValidateAudience = true,
			ValidAudience = oidcOptions.AccessTokenAudience,
			ValidateIssuerSigningKey = true,
			IssuerSigningKey = signingKey,
			ValidateLifetime = true,
			ClockSkew = TimeSpan.FromMinutes(1),
			NameClaimType = "name",
			RoleClaimType = "role"
		};

		var principal = tokenHandler.ValidateToken(token, validationParameters, out _);

		return Results.Json(new
		{
			sub = subject,
			name = principal.FindFirstValue("name") ?? string.Empty,
			preferred_username = principal.FindFirstValue("preferred_username") ?? string.Empty,
			role = principal.Claims.Where(claim => claim.Type == "role").Select(claim => claim.Value).ToArray()
		});
	}
	catch
	{
		return Results.Unauthorized();
	}
});

app.MapMethods("/endsession", new[] { "GET", "POST" }, (HttpRequest request) =>
{
	var postLogoutRedirectUri = request.Query["post_logout_redirect_uri"].ToString();

	if (string.IsNullOrWhiteSpace(postLogoutRedirectUri))
	{
		return Results.Redirect("/");
	}

	var isAllowed = validClients.Values.Any(client =>
		client.PostLogoutRedirectUris.Contains(postLogoutRedirectUri, StringComparer.OrdinalIgnoreCase));

	return isAllowed
		? Results.Redirect(postLogoutRedirectUri)
		: Results.Redirect("/");
});

app.Run();

static string ResolvePathFromContentRoot(string contentRootPath, string path)
{
	if (Path.IsPathRooted(path))
	{
		return path;
	}

	return Path.GetFullPath(Path.Combine(contentRootPath, path));
}

internal sealed record AuthorizationCodeEntry(
	string ClientId,
	string RedirectUri,
	string Scope,
	string Nonce,
	string Subject,
	string DisplayName,
	string Username,
	IReadOnlyList<string> Roles,
	DateTimeOffset ExpiresUtc);

public sealed class MockOidcOptions
{
	public const string SectionName = "MockOidc";

	public string Issuer { get; set; } = "http://localhost:5020";

	public string AccessTokenAudience { get; set; } = "gateway-api";

	public string ApiScope { get; set; } = "gateway.api";

	public string SigningCertificatePath { get; set; } = "../certs/dev-signing-cert.pem";

	public string SigningPrivateKeyPath { get; set; } = "../certs/dev-signing-key.pem";

	public string DefaultUsername { get; set; } = "pds-reader-user";

	public List<MockOidcUser> Users { get; set; } =
	[
		new MockOidcUser
		{
			Username = "pds-reader-user",
			Subject = "demo-reader-sub",
			DisplayName = "PDS Reader User",
			Roles = ["pds-reader"]
		},
		new MockOidcUser
		{
			Username = "pds-operator-user",
			Subject = "demo-operator-sub",
			DisplayName = "PDS Operator User",
			Roles = ["pds-reader", "pds-operator"]
		},
		new MockOidcUser
		{
			Username = "no-roles-user",
			Subject = "demo-noroles-sub",
			DisplayName = "No Roles User",
			Roles = []
		}
	];

	public List<MockOidcClient> Clients { get; set; } =
	[
		new MockOidcClient
		{
			ClientId = "demo-client",
			RedirectUris =
			[
				"http://localhost:5072/signin-oidc",
				"https://localhost:7179/signin-oidc"
			],
			PostLogoutRedirectUris =
			[
				"http://localhost:5072/",
				"https://localhost:7179/"
			]
		},
		new MockOidcClient
		{
			ClientId = "gateway-client",
			RedirectUris =
			[
				"http://localhost:5298/signin-oidc",
				"https://localhost:7152/signin-oidc"
			],
			PostLogoutRedirectUris =
			[
				"http://localhost:5298/",
				"https://localhost:7152/"
			]
		}
	];
}

public sealed class MockOidcClient
{
	public string ClientId { get; set; } = string.Empty;

	public List<string> RedirectUris { get; set; } = [];

	public List<string> PostLogoutRedirectUris { get; set; } = [];
}

public sealed class MockOidcUser
{
	public string Username { get; set; } = string.Empty;

	public string Subject { get; set; } = string.Empty;

	public string DisplayName { get; set; } = string.Empty;

	public List<string> Roles { get; set; } = [];
}
