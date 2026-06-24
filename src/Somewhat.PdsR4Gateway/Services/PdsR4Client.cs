using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Somewhat.PdsR4Gateway.Configuration;
using Somewhat.RateLimitedClient;

namespace Somewhat.PdsR4Gateway.Services;

public interface IPdsR4Client
{
    Task<JsonNode?> GetPatientByNhsNumberAsync(
        string nhsNumber,
        RequestPriority priority,
        CancellationToken cancellationToken = default);

    Task<JsonNode?> SearchPatientsAsync(
        string? family,
        string? given,
        string? birthdate,
        RequestPriority priority,
        CancellationToken cancellationToken = default);
}

public sealed class PdsR4Client : IPdsR4Client
{
    private readonly IRateLimitedApiClient _rateLimitedApiClient;
    private readonly PdsApiOptions _options;

    public PdsR4Client(
        IRateLimitedApiClient rateLimitedApiClient,
        IOptions<PdsApiOptions> options)
    {
        _rateLimitedApiClient = rateLimitedApiClient;
        _options = options.Value;
    }

    public Task<JsonNode?> GetPatientByNhsNumberAsync(
        string nhsNumber,
        RequestPriority priority,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nhsNumber))
        {
            throw new ArgumentException("An NHS number must be provided.", nameof(nhsNumber));
        }

        var route = _options.PatientByNhsNumberRouteTemplate.Replace("{nhsNumber}", Uri.EscapeDataString(nhsNumber));
        return SendAsync(route, priority, cancellationToken);
    }

    public Task<JsonNode?> SearchPatientsAsync(
        string? family,
        string? given,
        string? birthdate,
        RequestPriority priority,
        CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, string?>();

        if (!string.IsNullOrWhiteSpace(family)) query["family"] = family;
        if (!string.IsNullOrWhiteSpace(given)) query["given"] = given;
        if (!string.IsNullOrWhiteSpace(birthdate)) query["birthdate"] = birthdate;

        var route = QueryHelpers.AddQueryString(_options.SearchRoute, query);
        return SendAsync(route, priority, cancellationToken);
    }

    private Task<JsonNode?> SendAsync(
        string route,
        RequestPriority priority,
        CancellationToken cancellationToken)
    {
        return _rateLimitedApiClient.EnqueueAsync(async (http, token) =>
        {
            using var response = await http.GetAsync(route, token);
            var payload = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"PDS R4 call failed ({(int)response.StatusCode}): {payload}",
                    null,
                    response.StatusCode);
            }

            return string.IsNullOrWhiteSpace(payload)
                ? null
                : JsonNode.Parse(payload);
        }, priority, cancellationToken);
    }
}
