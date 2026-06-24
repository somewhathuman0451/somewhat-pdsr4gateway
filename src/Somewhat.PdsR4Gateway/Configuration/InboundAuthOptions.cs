namespace Somewhat.PdsR4Gateway.Configuration;

public sealed class InboundAuthOptions
{
    public const string SectionName = "InboundAuth";

    public string Issuer { get; set; } = "somewhat-pds-gateway";

    public string Audience { get; set; } = "somewhat-pds-gateway-clients";

    public string JwtSigningKey { get; set; } = string.Empty;
}
