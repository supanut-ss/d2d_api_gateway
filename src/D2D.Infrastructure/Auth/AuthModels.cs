using System.ComponentModel.DataAnnotations;

namespace D2D.Infrastructure.Auth;

public sealed record AuthTokens(string AccessToken, string RefreshToken, int ExpiresIn, string TokenType = "Bearer");

public sealed record RegisterRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MinLength(8), MaxLength(128)] string Password,
    [Required, MaxLength(200)] string DisplayName);

public sealed record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password);

public sealed record RefreshRequest([Required] string RefreshToken);

/// <summary>LIFF / LINE SDK login: the client already holds a LINE ID token.</summary>
public sealed record LineIdTokenLoginRequest([Required] string IdToken, string? Nonce);

/// <summary>Who a verified LINE ID token belongs to. No D2D user exists or is created.</summary>
public sealed record LineIdentityResponse(
    string UserId,
    string ProviderId,
    string ChannelId,
    string? DisplayName,
    string? PictureUrl,
    string? Email,
    DateTime? ExpiresAt);

/// <summary>Exchanges the one-time code returned to the app after the LINE web login redirect.</summary>
public sealed record ExchangeCodeRequest([Required] string Code);

/// <summary>
/// Starts a browser flow that links a LINE account from any registered LINE Login channel
/// (possibly another app's provider) to the signed-in user.
/// </summary>
public sealed record StartLineLinkRequest([Required] string ChannelId, [Required] string RedirectUri, string? State);

public sealed record StartLineLinkResponse(string AuthorizeUrl);

public sealed record LinkedIdentity(string Provider, string ProviderScope, string ChannelId, DateTime LinkedAt);

public sealed record MeResponse(
    Guid Id,
    string DisplayName,
    string? Email,
    string? PictureUrl,
    string App,
    IReadOnlyList<string> Roles,
    IReadOnlyList<LinkedIdentity> Identities);
