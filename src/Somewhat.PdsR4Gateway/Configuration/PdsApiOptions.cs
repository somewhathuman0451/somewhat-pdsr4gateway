namespace Somewhat.PdsR4Gateway.Configuration;

public sealed class PdsApiOptions
{
    public const string SectionName = "PdsApi";

    public const string HttpClientName = "PdsR4Client";

    public string BaseAddress { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string CertificateName { get; set; } = "default";

    public string PatientByNhsNumberRouteTemplate { get; set; } = "Patient/{nhsNumber}";

    public string SearchRoute { get; set; } = "Patient";
}
