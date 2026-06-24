using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GatewayDemoOptions>(builder.Configuration.GetSection(GatewayDemoOptions.SectionName));
var gatewayDemoOptions = builder.Configuration.GetSection(GatewayDemoOptions.SectionName).Get<GatewayDemoOptions>()
	?? new GatewayDemoOptions();

builder.Services
	.AddAuthentication(options =>
	{
		options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
		options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
	})
	.AddCookie(options =>
	{
		options.Cookie.Name = "Somewhat.PdsGateway.Demo.Auth";
	})
	.AddOpenIdConnect(options =>
	{
		options.Authority = gatewayDemoOptions.OidcAuthority;
		options.RequireHttpsMetadata = gatewayDemoOptions.RequireHttpsMetadata;
		options.ClientId = gatewayDemoOptions.OidcClientId;
		options.ResponseType = "code";
		options.UsePkce = true;
		options.SaveTokens = true;
		options.GetClaimsFromUserInfoEndpoint = true;
		options.Scope.Clear();
		options.Scope.Add("openid");
		options.Scope.Add("profile");
		options.Scope.Add(gatewayDemoOptions.OidcScope);
		options.Events.OnRedirectToIdentityProvider = context =>
		{
			if (context.Properties.Items.TryGetValue("login_hint", out var loginHint) &&
				!string.IsNullOrWhiteSpace(loginHint))
			{
				context.ProtocolMessage.LoginHint = loginHint;
			}

			return Task.CompletedTask;
		};
	});

builder.Services.AddAuthorization();
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", (IOptions<GatewayDemoOptions> options) => Results.Content(BuildUiPage(options.Value), "text/html"));

app.MapGet("/auth/login", (string? returnUrl, string? user) =>
{
    var props = new AuthenticationProperties
    {
        RedirectUri = returnUrl ?? "/"
    };

	if (!string.IsNullOrWhiteSpace(user))
	{
		props.Items["login_hint"] = user;
	}

    return Results.Challenge(props, [OpenIdConnectDefaults.AuthenticationScheme]);
}).AllowAnonymous();

app.MapGet("/auth/logout", async (HttpContext context, string? returnUrl) =>
{
	await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

	var props = new AuthenticationProperties
	{
		RedirectUri = returnUrl ?? "/"
	};

	return Results.SignOut(props, [OpenIdConnectDefaults.AuthenticationScheme]);
});

app.MapGet("/me", async (HttpContext context) =>
{
	if (context.User.Identity?.IsAuthenticated != true)
	{
		return Results.Unauthorized();
	}

	var authResult = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
	var accessToken = authResult.Properties?.GetTokenValue("access_token");

	return Results.Ok(new
	{
		name = context.User.Identity?.Name,
		claims = context.User.Claims.Select(c => new { c.Type, c.Value }),
		hasAccessToken = !string.IsNullOrWhiteSpace(accessToken)
	});
});

app.MapGet("/demo/patient/{nhsNumber}", async (
	string nhsNumber,
	IHttpClientFactory httpClientFactory,
	IOptions<GatewayDemoOptions> options,
	HttpContext context,
	CancellationToken cancellationToken) =>
{
	var token = await ResolveAccessTokenAsync(context);
	if (string.IsNullOrWhiteSpace(token))
	{
		return Results.Unauthorized();
	}

	using var client = httpClientFactory.CreateClient();
	client.BaseAddress = new Uri(options.Value.GatewayBaseAddress, UriKind.Absolute);
	client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
		"Bearer",
		token);

	using var response = await client.GetAsync($"/pds/patient/{Uri.EscapeDataString(nhsNumber)}", cancellationToken);
	var payload = await response.Content.ReadAsStringAsync(cancellationToken);

	return Results.Json(new
	{
		statusCode = (int)response.StatusCode,
		payload
	});
});

app.MapGet("/demo/search", async (
	string? family,
	string? given,
	string? birthdate,
	IHttpClientFactory httpClientFactory,
	IOptions<GatewayDemoOptions> options,
	HttpContext context,
	CancellationToken cancellationToken) =>
{
	var token = await ResolveAccessTokenAsync(context);
	if (string.IsNullOrWhiteSpace(token))
	{
		return Results.Unauthorized();
	}

	var qs = new List<string>();
	if (!string.IsNullOrWhiteSpace(family)) qs.Add($"family={Uri.EscapeDataString(family)}");
	if (!string.IsNullOrWhiteSpace(given)) qs.Add($"given={Uri.EscapeDataString(given)}");
	if (!string.IsNullOrWhiteSpace(birthdate)) qs.Add($"birthdate={Uri.EscapeDataString(birthdate)}");

	using var client = httpClientFactory.CreateClient();
	client.BaseAddress = new Uri(options.Value.GatewayBaseAddress, UriKind.Absolute);
	client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
		"Bearer",
		token);

	var url = "/pds/patient" + (qs.Count == 0 ? string.Empty : $"?{string.Join("&", qs)}");
	using var response = await client.GetAsync(url, cancellationToken);
	var payload = await response.Content.ReadAsStringAsync(cancellationToken);

	return Results.Json(new
	{
		statusCode = (int)response.StatusCode,
		payload
	});
});

app.MapGet("/demo/queue-depth", async (
	IHttpClientFactory httpClientFactory,
	IOptions<GatewayDemoOptions> options,
	HttpContext context,
	CancellationToken cancellationToken) =>
{
	var token = await ResolveAccessTokenAsync(context);
	if (string.IsNullOrWhiteSpace(token))
	{
		return Results.Unauthorized();
	}

	using var client = httpClientFactory.CreateClient();
	client.BaseAddress = new Uri(options.Value.GatewayBaseAddress, UriKind.Absolute);
	client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

	using var response = await client.GetAsync("/pds/queue-depth", cancellationToken);
	var payload = await response.Content.ReadAsStringAsync(cancellationToken);

	return Results.Json(new
	{
		statusCode = (int)response.StatusCode,
		payload
	});
});

app.MapGet("/demo/tps-single-patient", async (
	IHttpClientFactory httpClientFactory,
	IOptions<GatewayDemoOptions> options,
	HttpContext context,
	int count,
	string? nhsNumber,
	CancellationToken cancellationToken) =>
{
	var token = await ResolveAccessTokenAsync(context);
	if (string.IsNullOrWhiteSpace(token))
	{
		return Results.Unauthorized();
	}

	if (count <= 0) count = 1;
	if (count > 50) count = 50;

	var patientNumber = string.IsNullOrWhiteSpace(nhsNumber) ? "9000000009" : nhsNumber;

	using var client = httpClientFactory.CreateClient();
	client.BaseAddress = new Uri(options.Value.GatewayBaseAddress, UriKind.Absolute);
	client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

	var started = DateTimeOffset.UtcNow;
	var stopwatch = System.Diagnostics.Stopwatch.StartNew();

	var tasks = Enumerable.Range(1, count).Select(async i =>
	{
		var requestStartedMs = stopwatch.ElapsedMilliseconds;
		using var response = await client.GetAsync($"/pds/patient/{Uri.EscapeDataString(patientNumber)}", cancellationToken);
		var payload = await response.Content.ReadAsStringAsync(cancellationToken);
		var completedMs = stopwatch.ElapsedMilliseconds;

		return new
		{
			request = i,
			statusCode = (int)response.StatusCode,
			startedAtMs = requestStartedMs,
			completedAtMs = completedMs,
			payloadPreview = payload.Length <= 120 ? payload : payload[..120] + "..."
		};
	}).ToArray();

	var results = await Task.WhenAll(tasks);
	stopwatch.Stop();

	var elapsedMs = stopwatch.ElapsedMilliseconds;
	var expectedTps = Math.Max(1, options.Value.ExpectedGatewayTps);
	var expectedMinMs = ((count - 1) / expectedTps) * 1000;

	var statusGroups = results
		.GroupBy(result => result.statusCode)
		.ToDictionary(group => group.Key, group => group.Count());

	var allSuccess = results.All(result => result.statusCode is >= 200 and < 300);
	var guidance = allSuccess
		? "TPS evidence is valid because all upstream calls succeeded."
		: "Some calls were non-success. For TPS demonstration use a role with read permission (for example pds-reader-user).";

	var completionTimeline = results
		.OrderBy(result => result.completedAtMs)
		.Select(result => new { result.request, result.statusCode, result.completedAtMs })
		.ToArray();

	return Results.Ok(new
	{
		summary = new
		{
			patientNumber,
			requestCount = count,
			expectedGatewayTps = expectedTps,
			startedUtc = started,
			elapsedMs,
			expectedMinimumMsForThrottle = expectedMinMs,
			throttleLikelyObserved = elapsedMs >= expectedMinMs,
			statusGroups,
			guidance
		},
		completionTimeline,
		requests = results
	});
});

app.MapGet("/demo/tps-single-patient-stream", async (
	IHttpClientFactory httpClientFactory,
	IOptions<GatewayDemoOptions> options,
	HttpContext context,
	int count,
	string? nhsNumber,
	CancellationToken cancellationToken) =>
{
	var token = await ResolveAccessTokenAsync(context);
	if (string.IsNullOrWhiteSpace(token))
	{
		context.Response.StatusCode = StatusCodes.Status401Unauthorized;
		await context.Response.WriteAsJsonAsync(new { error = "Not authenticated" }, cancellationToken);
		return;
	}

	if (count <= 0) count = 1;
	if (count > 100) count = 100;

	var patientNumber = string.IsNullOrWhiteSpace(nhsNumber) ? "9000000009" : nhsNumber;

	context.Response.Headers.CacheControl = "no-cache";
	context.Response.Headers.Append("X-Accel-Buffering", "no");
	context.Response.ContentType = "text/event-stream";

	var writerLock = new SemaphoreSlim(1, 1);

	async Task WriteEventAsync(string eventName, object payload)
	{
		var json = JsonSerializer.Serialize(payload);
		await writerLock.WaitAsync(cancellationToken);
		try
		{
			await context.Response.WriteAsync($"event: {eventName}\n", cancellationToken);
			await context.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
			await context.Response.Body.FlushAsync(cancellationToken);
		}
		finally
		{
			writerLock.Release();
		}
	}

	using var client = httpClientFactory.CreateClient();
	client.BaseAddress = new Uri(options.Value.GatewayBaseAddress, UriKind.Absolute);
	client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

	var started = DateTimeOffset.UtcNow;
	var stopwatch = System.Diagnostics.Stopwatch.StartNew();
	var statusGroups = new ConcurrentDictionary<int, int>();
	var results = new ConcurrentBag<TpsRequestResult>();
	var completedRequests = 0;

	await WriteEventAsync("started", new
	{
		patientNumber,
		requestCount = count,
		expectedGatewayTps = Math.Max(1, options.Value.ExpectedGatewayTps),
		startedUtc = started
	});

	var tasks = Enumerable.Range(1, count).Select(async i =>
	{
		var requestStartedMs = stopwatch.ElapsedMilliseconds;
		int statusCode;
		string payloadPreview;

		try
		{
			using var response = await client.GetAsync($"/pds/patient/{Uri.EscapeDataString(patientNumber)}", cancellationToken);
			var payload = await response.Content.ReadAsStringAsync(cancellationToken);
			statusCode = (int)response.StatusCode;
			payloadPreview = payload.Length <= 120 ? payload : payload[..120] + "...";
		}
		catch (Exception ex)
		{
			statusCode = 0;
			payloadPreview = ex.Message.Length <= 120 ? ex.Message : ex.Message[..120] + "...";
		}

		var completedAtMs = stopwatch.ElapsedMilliseconds;
		var completeNow = Interlocked.Increment(ref completedRequests);
		var elapsedSeconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
		var currentThroughput = Math.Round(completeNow / elapsedSeconds, 3);

		statusGroups.AddOrUpdate(statusCode, 1, (_, value) => value + 1);

		var requestResult = new TpsRequestResult
		{
			Request = i,
			StatusCode = statusCode,
			StartedAtMs = requestStartedMs,
			CompletedAtMs = completedAtMs,
			PayloadPreview = payloadPreview
		};
		results.Add(requestResult);

		await WriteEventAsync("progress", new
		{
			request = i,
			statusCode,
			startedAtMs = requestStartedMs,
			completedAtMs,
			completed = completeNow,
			total = count,
			throughputPerSecond = currentThroughput,
			statusGroups = statusGroups.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value)
		});
	}).ToArray();

	await Task.WhenAll(tasks);
	stopwatch.Stop();

	var finalResults = results.OrderBy(result => result.CompletedAtMs).ToArray();
	var elapsedMs = stopwatch.ElapsedMilliseconds;
	var expectedTps = Math.Max(1, options.Value.ExpectedGatewayTps);
	var expectedMinMs = ((count - 1) / expectedTps) * 1000;
	var allSuccess = finalResults.All(result => result.StatusCode is >= 200 and < 300);
	var guidance = allSuccess
		? "TPS evidence is valid because all upstream calls succeeded."
		: "Some calls were non-success. For TPS demonstration use a role with read permission (for example pds-reader-user).";

	await WriteEventAsync("completed", new
	{
		summary = new
		{
			patientNumber,
			requestCount = count,
			expectedGatewayTps = expectedTps,
			startedUtc = started,
			elapsedMs,
			expectedMinimumMsForThrottle = expectedMinMs,
			throttleLikelyObserved = elapsedMs >= expectedMinMs,
			statusGroups = statusGroups.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value),
			guidance
		},
		completionTimeline = finalResults.Select(result => new { result.Request, result.StatusCode, result.CompletedAtMs }),
		requests = finalResults
	});

	await WriteEventAsync("done", new { ok = true });
});

app.Run();

static string BuildUiPage(GatewayDemoOptions options)
{
	var gatewayUrl = options.GatewayBaseAddress;
	return $$"""
<!doctype html>
<html lang="en">
<head>
	<meta charset="utf-8" />
	<meta name="viewport" content="width=device-width, initial-scale=1" />
	<title>Somewhat PDS Gateway Demo</title>
	<style>
		:root {
			--bg: #f4efe8;
			--ink: #1c2026;
			--muted: #5d6776;
			--card: #ffffff;
			--accent: #005eb8;
			--accent-2: #007f3b;
			--danger: #c92a2a;
			--border: #d8dde6;
		}
		* { box-sizing: border-box; }
		body {
			margin: 0;
			font-family: "IBM Plex Sans", "Segoe UI", sans-serif;
			color: var(--ink);
			background: radial-gradient(circle at 20% -10%, #fff7e8, var(--bg));
			min-height: 100vh;
		}
		.wrap {
			max-width: 980px;
			margin: 2rem auto;
			padding: 0 1rem;
			display: grid;
			gap: 1rem;
		}
		.card {
			background: var(--card);
			border: 1px solid var(--border);
			border-radius: 14px;
			padding: 1rem;
		}
		h1 { margin: 0; font-size: 1.6rem; }
		h2 { margin: 0 0 0.5rem; font-size: 1.1rem; }
		p { margin: 0.3rem 0; color: var(--muted); }
		.row { display: flex; flex-wrap: wrap; gap: 0.5rem; margin-top: 0.6rem; }
		button, .linkbtn {
			border: 0;
			border-radius: 999px;
			padding: 0.55rem 0.9rem;
			background: var(--accent);
			color: #fff;
			cursor: pointer;
			text-decoration: none;
			font-weight: 600;
		}
		button.secondary, .linkbtn.secondary { background: #5b6675; }
		button.success, .linkbtn.success { background: var(--accent-2); }
		button.danger, .linkbtn.danger { background: var(--danger); }
		pre {
			margin: 0;
			padding: 0.8rem;
			border-radius: 10px;
			background: #111827;
			color: #d1e2ff;
			overflow: auto;
			max-height: 320px;
			font-size: 0.82rem;
		}
		.status { font-size: 0.9rem; color: var(--muted); }
		.controls {
			display: grid;
			grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
			gap: 0.6rem;
			margin-top: 0.7rem;
		}
		label { display: grid; gap: 0.25rem; font-size: 0.85rem; color: var(--muted); }
		input {
			border: 1px solid var(--border);
			border-radius: 8px;
			padding: 0.45rem 0.55rem;
			font: inherit;
			color: var(--ink);
			background: #fff;
		}
		#tps-graph {
			width: 100%;
			height: 240px;
			border: 1px solid var(--border);
			border-radius: 10px;
			background: linear-gradient(180deg, #ffffff, #f5f9ff);
		}
		#tps-bucket-graph {
			width: 100%;
			height: 240px;
			border: 1px solid var(--border);
			border-radius: 10px;
			background: linear-gradient(180deg, #ffffff, #f7fff9);
		}
		.graphs {
			display: grid;
			grid-template-columns: repeat(auto-fit, minmax(300px, 1fr));
			gap: 0.75rem;
			margin-top: 0.7rem;
		}
		.graph-meta {
			margin-top: 0.5rem;
			font-size: 0.88rem;
			color: var(--muted);
		}
	</style>
</head>
<body>
	<div class="wrap">
		<div class="card">
			<h1>Somewhat PDS Gateway Demo</h1>
			<p>Gateway URL: {{gatewayUrl}}</p>
			<p class="status" id="session-status">Checking session...</p>
			<div class="row">
				<a class="linkbtn" href="/auth/login?user=pds-reader-user&returnUrl=/">Login as pds-reader</a>
				<a class="linkbtn" href="/auth/login?user=pds-searcher-user&returnUrl=/">Login as pds-searcher</a>
				<a class="linkbtn success" href="/auth/login?user=pds-operator-user&returnUrl=/">Login as pds-operator</a>
				<a class="linkbtn danger" href="/auth/login?user=no-roles-user&returnUrl=/">Login as no roles</a>
				<a class="linkbtn secondary" href="/auth/logout?returnUrl=/">Logout</a>
			</div>
		</div>

		<div class="card">
			<h2>Checks</h2>
			<div class="row">
				<button onclick="callEndpoint('/me')">/me</button>
				<button onclick="callEndpoint('/demo/patient/9000000009')">patient read</button>
				<button onclick="callEndpoint('/demo/search?family=SMITH&given=JOHN')">patient search</button>
				<button onclick="callEndpoint('/demo/queue-depth')">queue depth (operator only)</button>
			</div>

			<h2 style="margin-top: 1rem;">TPS Burst Demo</h2>
			<p>Use pds-reader for a clean throughput demonstration.</p>
			<div class="controls">
				<label>
					Request count
					<input id="tps-count" type="number" min="1" max="100" value="15" />
				</label>
				<label>
					NHS number
					<input id="tps-nhs" type="text" value="9000000009" />
				</label>
			</div>
			<div class="row">
				<button class="success" id="tps-run" onclick="runTpsDemoLive()">Run live TPS demo</button>
			</div>
			<div class="graphs">
				<canvas id="tps-graph" width="920" height="240"></canvas>
				<canvas id="tps-bucket-graph" width="920" height="240"></canvas>
			</div>
			<div class="graph-meta" id="tps-meta">No live run in progress.</div>
		</div>

		<div class="card">
			<h2>Output</h2>
			<pre id="output">Run a check after logging in.</pre>
		</div>
	</div>

	<script>
		let activeTpsSource = null;
		let tpsPoints = [];
		let bucketCompletions = new Map();
		let expectedTps = 5;

		function drawTpsGraph() {
			const canvas = document.getElementById('tps-graph');
			const ctx = canvas.getContext('2d');
			const w = canvas.width;
			const h = canvas.height;
			ctx.clearRect(0, 0, w, h);

			ctx.fillStyle = '#f5f9ff';
			ctx.fillRect(0, 0, w, h);

			const pad = 34;
			const innerW = w - (pad * 2);
			const innerH = h - (pad * 2);
			const maxX = Math.max(1, ...tpsPoints.map(p => p.t));
			const maxY = Math.max(expectedTps * 1.2, 1, ...tpsPoints.map(p => p.v));

			ctx.strokeStyle = '#d8dde6';
			ctx.lineWidth = 1;
			for (let i = 0; i <= 4; i++) {
				const y = pad + (innerH / 4) * i;
				ctx.beginPath();
				ctx.moveTo(pad, y);
				ctx.lineTo(pad + innerW, y);
				ctx.stroke();
			}

			ctx.strokeStyle = '#007f3b';
			ctx.lineWidth = 2;
			const expectedY = pad + innerH - ((expectedTps / maxY) * innerH);
			ctx.beginPath();
			ctx.moveTo(pad, expectedY);
			ctx.lineTo(pad + innerW, expectedY);
			ctx.stroke();

			ctx.strokeStyle = '#005eb8';
			ctx.lineWidth = 2;
			ctx.beginPath();
			tpsPoints.forEach((point, index) => {
				const x = pad + ((point.t / maxX) * innerW);
				const y = pad + innerH - ((point.v / maxY) * innerH);
				if (index === 0) ctx.moveTo(x, y);
				else ctx.lineTo(x, y);
			});
			ctx.stroke();

			ctx.fillStyle = '#1c2026';
			ctx.font = '12px "IBM Plex Sans", sans-serif';
			ctx.fillText('Throughput (req/s)', pad, 16);
			ctx.fillText(`Expected ${expectedTps.toFixed(1)} req/s`, pad + 10, Math.max(18, expectedY - 6));
			ctx.fillText(`${maxX.toFixed(1)}s`, w - pad - 28, h - 10);
		}

		function resetTpsGraph() {
			tpsPoints = [];
			bucketCompletions = new Map();
			drawTpsGraph();
			drawBucketGraph();
		}

		function drawBucketGraph() {
			const canvas = document.getElementById('tps-bucket-graph');
			const ctx = canvas.getContext('2d');
			const w = canvas.width;
			const h = canvas.height;
			ctx.clearRect(0, 0, w, h);

			ctx.fillStyle = '#f7fff9';
			ctx.fillRect(0, 0, w, h);

			const pad = 34;
			const innerW = w - (pad * 2);
			const innerH = h - (pad * 2);

			const seconds = [...bucketCompletions.keys()].sort((a, b) => a - b);
			const maxSecond = Math.max(1, ...(seconds.length ? seconds : [0]));
			const maxCount = Math.max(1, ...seconds.map(s => bucketCompletions.get(s) || 0), expectedTps);

			ctx.strokeStyle = '#d8dde6';
			ctx.lineWidth = 1;
			for (let i = 0; i <= 4; i++) {
				const y = pad + (innerH / 4) * i;
				ctx.beginPath();
				ctx.moveTo(pad, y);
				ctx.lineTo(pad + innerW, y);
				ctx.stroke();
			}

			if (seconds.length > 0) {
				const barGap = 6;
				const slotW = innerW / (maxSecond + 1);
				const barW = Math.max(3, slotW - barGap);

				ctx.fillStyle = '#007f3b';
				for (const second of seconds) {
					const count = bucketCompletions.get(second) || 0;
					const x = pad + (second * slotW) + (slotW - barW) / 2;
					const y = pad + innerH - ((count / maxCount) * innerH);
					ctx.fillRect(x, y, barW, pad + innerH - y);
				}
			}

			ctx.strokeStyle = '#005eb8';
			ctx.lineWidth = 2;
			const expectedY = pad + innerH - ((expectedTps / maxCount) * innerH);
			ctx.beginPath();
			ctx.moveTo(pad, expectedY);
			ctx.lineTo(pad + innerW, expectedY);
			ctx.stroke();

			ctx.fillStyle = '#1c2026';
			ctx.font = '12px "IBM Plex Sans", sans-serif';
			ctx.fillText('Completed requests per second', pad, 16);
			ctx.fillText(`Expected ${expectedTps.toFixed(1)} req/s`, pad + 10, Math.max(18, expectedY - 6));
			ctx.fillText(`${maxSecond}s`, w - pad - 22, h - 10);
		}

		async function refreshSession() {
			const status = document.getElementById('session-status');
			try {
				const res = await fetch('/me', { credentials: 'include' });
				if (res.status === 200) {
					const data = await res.json();
					const roles = (data.claims || [])
						.filter(c => c.type.endsWith('/role') || c.type === 'role')
						.map(c => c.value);
					status.textContent = `Logged in user=${data.name ?? '(none)'} roles=[${roles.join(', ')}]`;
				} else {
					status.textContent = 'Not authenticated. Choose a login role above.';
				}
			} catch (err) {
				status.textContent = 'Session check failed: ' + err;
			}
		}

		async function callEndpoint(path) {
			const out = document.getElementById('output');
			try {
				const res = await fetch(path, { credentials: 'include' });
				const text = await res.text();
				let body = text;
				try { body = JSON.parse(text); } catch {}
				out.textContent = JSON.stringify({ path, status: res.status, body }, null, 2);
			} catch (err) {
				out.textContent = String(err);
			}
			refreshSession();
		}

		async function runTpsDemo() {
			const countInput = document.getElementById('tps-count');
			const nhsInput = document.getElementById('tps-nhs');
			const runButton = document.getElementById('tps-run');
			const out = document.getElementById('output');
			const meta = document.getElementById('tps-meta');

			const count = Math.max(1, Math.min(100, Number.parseInt(countInput.value, 10) || 15));
			const nhs = (nhsInput.value || '9000000009').trim();

			if (activeTpsSource) {
				activeTpsSource.close();
				activeTpsSource = null;
			}

			resetTpsGraph();
			out.textContent = 'Starting live TPS stream...';
			meta.textContent = `Running ${count} requests to patient ${nhs}.`;
			runButton.disabled = true;

			const qs = new URLSearchParams({ count: String(count), nhsNumber: nhs });
			const source = new EventSource(`/demo/tps-single-patient-stream?${qs.toString()}`);
			activeTpsSource = source;

			source.addEventListener('started', event => {
				const data = JSON.parse(event.data);
				expectedTps = Number(data.expectedGatewayTps || 1);
				meta.textContent = `Started at ${data.startedUtc}; expected TPS ${expectedTps}.`;
				drawTpsGraph();
				drawBucketGraph();
			});

			source.addEventListener('progress', event => {
				const data = JSON.parse(event.data);
				tpsPoints.push({ t: Number(data.completedAtMs || 0) / 1000, v: Number(data.throughputPerSecond || 0) });
				const secondBucket = Math.floor(Number(data.completedAtMs || 0) / 1000);
				const currentBucketValue = bucketCompletions.get(secondBucket) || 0;
				bucketCompletions.set(secondBucket, currentBucketValue + 1);
				drawTpsGraph();
				drawBucketGraph();
				const peakBucket = Math.max(...bucketCompletions.values());
				meta.textContent = `Completed ${data.completed}/${data.total} requests. Current throughput ${Number(data.throughputPerSecond).toFixed(2)} req/s. Peak bucket ${peakBucket} req/s.`;
				out.textContent = JSON.stringify({ lastProgress: data }, null, 2);
			});

			source.addEventListener('completed', event => {
				const data = JSON.parse(event.data);
				meta.textContent = `Run complete in ${data.summary.elapsedMs} ms. Throttle observed=${data.summary.throttleLikelyObserved}.`;
				out.textContent = JSON.stringify(data, null, 2);
			});

			source.addEventListener('done', () => {
				source.close();
				if (activeTpsSource === source) {
					activeTpsSource = null;
				}
				runButton.disabled = false;
				refreshSession();
			});

			source.onerror = () => {
				if (activeTpsSource === source) {
					meta.textContent = 'Live TPS stream ended unexpectedly.';
					out.textContent = 'Live TPS stream ended unexpectedly. Ensure you are logged in and retry.';
					activeTpsSource = null;
					runButton.disabled = false;
				}
				source.close();
			};
		}

		window.runTpsDemoLive = runTpsDemo;
		resetTpsGraph();
		refreshSession();
	</script>
</body>
</html>
""";
}

static async Task<string?> ResolveAccessTokenAsync(HttpContext context)
{
	if (context.User.Identity?.IsAuthenticated != true)
	{
		return null;
	}

	var authResult = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
	return authResult.Properties?.GetTokenValue("access_token");
}

public sealed class GatewayDemoOptions
{
	public const string SectionName = "GatewayDemo";

	public string GatewayBaseAddress { get; set; } = "http://localhost:5298";

	public string OidcAuthority { get; set; } = "http://localhost:5020";

	public string OidcClientId { get; set; } = "demo-client";

	public string OidcScope { get; set; } = "gateway.api";

	public int ExpectedGatewayTps { get; set; } = 5;

	public bool RequireHttpsMetadata { get; set; }
}

public sealed class TpsRequestResult
{
	public int Request { get; set; }

	public int StatusCode { get; set; }

	public long StartedAtMs { get; set; }

	public long CompletedAtMs { get; set; }

	public string PayloadPreview { get; set; } = string.Empty;
}
