namespace Edgewise.Infrastructure.Auth;

/// <summary>
/// Exception carrying an HTTP status and stable machine-readable code. The API
/// error middleware translates it to the standard {"error":{"code","message"}} shape.
/// </summary>
public sealed class ApiException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;

    public static ApiException BadRequest(string code, string message) => new(400, code, message);
    public static ApiException Unauthorized(string code, string message) => new(401, code, message);
    public static ApiException Forbidden(string code, string message) => new(403, code, message);
    public static ApiException NotFound(string code, string message) => new(404, code, message);
    public static ApiException Conflict(string code, string message) => new(409, code, message);
}
