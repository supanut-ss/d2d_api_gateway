namespace D2D.Core.Security;

public static class AppClaimTypes
{
    public const string Subject = "sub";
    public const string Name = "name";
    public const string Email = "email";
    public const string Role = "role";
    public const string AppId = "app_id";
    public const string AppCode = "app_code";
}

public static class AppHeaders
{
    /// <summary>App code identifying the calling app (tenant).</summary>
    public const string AppId = "X-App-Id";

    /// <summary>Server-to-server secret of an app.</summary>
    public const string ApiKey = "X-Api-Key";

    /// <summary>Platform administrator key.</summary>
    public const string AdminKey = "X-Admin-Key";
}

public static class AppRoles
{
    public const string User = "user";
    public const string AppServer = "app_server";

    /// <summary>Platform admin who may change things (admin key, or an admin console user with role Admin).</summary>
    public const string SuperAdmin = "super_admin";

    /// <summary>Admin console user who may only look.</summary>
    public const string PlatformViewer = "platform_viewer";
}
