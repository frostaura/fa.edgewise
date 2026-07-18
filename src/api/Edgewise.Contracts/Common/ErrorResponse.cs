namespace Edgewise.Contracts.Common;

/// <summary>The single error envelope used by every API error response.</summary>
public sealed record ErrorResponse(ErrorDetail Error);

public sealed record ErrorDetail(string Code, string Message);
