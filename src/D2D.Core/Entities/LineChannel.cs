namespace D2D.Core.Entities;

/// <summary>A LINE channel owned by an app. An app may own several (e.g. one Login + many Messaging).</summary>
public class LineChannel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AppId { get; set; }
    public LineChannelType Type { get; set; }

    /// <summary>Channel ID issued by the LINE Developers Console.</summary>
    public string ChannelId { get; set; } = default!;

    /// <summary>
    /// LINE provider the channel belongs to (the id in the console URL /console/provider/{id}).
    /// LINE user ids are identical across channels of one provider and differ across providers.
    /// </summary>
    public string ProviderId { get; set; } = default!;

    public string ChannelSecretEncrypted { get; set; } = default!;

    /// <summary>How the Messaging API channel access token is obtained.</summary>
    public LineTokenMode TokenMode { get; set; } = LineTokenMode.Static;

    /// <summary>Long-lived channel access token pasted from the console (<see cref="LineTokenMode.Static"/>).</summary>
    public string? AccessTokenEncrypted { get; set; }

    /// <summary>kid LINE returned when the assertion public key was registered (<see cref="LineTokenMode.JwtAssertion"/>).</summary>
    public string? AssertionKeyId { get; set; }

    /// <summary>Private key (JWK JSON) that signs the JWT assertion.</summary>
    public string? AssertionPrivateKeyEncrypted { get; set; }

    /// <summary>
    /// Last v2.1 token issued with the JWT assertion. Stored so every instance reuses it
    /// instead of issuing new ones (LINE allows at most 30 active v2.1 tokens per channel).
    /// </summary>
    public string? IssuedAccessTokenEncrypted { get; set; }

    public DateTime? IssuedAccessTokenExpiresAt { get; set; }

    /// <summary>Optional app backend URL that receives each webhook event after it is stored.</summary>
    public string? WebhookForwardUrl { get; set; }

    /// <summary>
    /// When true the owning app's backend may fetch this channel's secret and a usable access token
    /// (GET /api/v1/line/channels/{id}/credentials). Off by default so a leaked app API key cannot read secrets.
    /// </summary>
    public bool AllowCredentialAccess { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum LineChannelType
{
    Messaging = 1,
    Login = 2,
}

public enum LineTokenMode
{
    /// <summary>Long-lived token issued in the console; never expires until reissued.</summary>
    Static = 1,

    /// <summary>Stateless token (15 min) issued on demand from channel id + secret. No setup needed.</summary>
    Stateless = 2,

    /// <summary>Channel access token v2.1 issued with a signed JWT assertion; renewed automatically.</summary>
    JwtAssertion = 3,
}
