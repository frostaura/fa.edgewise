namespace Edgewise.Api.Endpoints;

/// <summary>
/// Discovered at startup and mapped automatically. Implementations must have a
/// parameterless constructor. Feature endpoint groups implement this instead of
/// editing Program.cs.
/// </summary>
public interface IEndpointModule
{
    void Map(IEndpointRouteBuilder app);
}
