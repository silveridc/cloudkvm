namespace Control.Model.Response;

public sealed record VncConsoleResponse(string Url, DateTimeOffset ExpiresAt);
