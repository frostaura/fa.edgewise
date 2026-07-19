using System.Text.Json;
using Edgewise.Api.Services.Journal;
using Edgewise.Domain.Entities;
using Edgewise.Infrastructure.Auth;

namespace Edgewise.Api.Endpoints;

/// <summary>
/// CSV import wizard: preview (multipart {file, venue?}) then commit
/// (multipart {file, mapping, accountId, venue, saveMappingAs?}). The file is
/// re-sent on commit, so nothing is persisted between the two steps.
/// </summary>
public sealed class ImportsEndpoints : IEndpointModule
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Map(IEndpointRouteBuilder app)
    {
        var imports = app.MapGroup("/api/imports").RequireAuthorization().DisableAntiforgery();

        imports.MapPost("/csv/preview", Preview);
        imports.MapPost("/csv/commit", Commit);
    }

    private static async Task<IResult> Preview(HttpRequest request, CsvImportService service, CancellationToken ct)
    {
        var form = await ReadFormAsync(request, ct);
        var file = RequireFile(form);
        Venue? venueHint = null;
        if (Enum.TryParse<Venue>(form["venue"].ToString(), ignoreCase: true, out var parsed))
        {
            venueHint = parsed;
        }

        await using var stream = file.OpenReadStream();
        return Results.Ok(await service.PreviewAsync(stream, venueHint, ct));
    }

    private static async Task<IResult> Commit(HttpRequest request, CsvImportService service, CancellationToken ct)
    {
        var form = await ReadFormAsync(request, ct);
        var file = RequireFile(form);

        if (!Guid.TryParse(form["accountId"].ToString(), out var accountId))
        {
            throw ApiException.BadRequest("account_required", "accountId form field is required.");
        }

        if (!Enum.TryParse<Venue>(form["venue"].ToString(), ignoreCase: true, out var venue))
        {
            venue = Venue.Generic;
        }

        Dictionary<string, string?> mapping;
        try
        {
            mapping = JsonSerializer.Deserialize<Dictionary<string, string?>>(form["mapping"].ToString(), Json)
                ?? throw ApiException.BadRequest("mapping_required", "mapping form field is required.");
        }
        catch (JsonException)
        {
            throw ApiException.BadRequest("invalid_mapping", "mapping must be a JSON object of field to column name.");
        }

        var saveMappingAs = form["saveMappingAs"].ToString();

        await using var stream = file.OpenReadStream();
        return Results.Ok(await service.CommitAsync(
            stream, mapping, accountId, venue,
            string.IsNullOrWhiteSpace(saveMappingAs) ? null : saveMappingAs, ct));
    }

    private static async Task<IFormCollection> ReadFormAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            throw ApiException.BadRequest("multipart_required", "Send multipart/form-data with a 'file' field.");
        }

        return await request.ReadFormAsync(ct);
    }

    private static IFormFile RequireFile(IFormCollection form)
    {
        var file = form.Files["file"] ?? form.Files.FirstOrDefault()
            ?? throw ApiException.BadRequest("file_required", "A CSV file is required.");
        if (file.Length == 0)
        {
            throw ApiException.BadRequest("file_empty", "The uploaded file is empty.");
        }

        if (file.Length > CsvImportService.MaxFileBytes)
        {
            throw ApiException.BadRequest("file_too_large", "Files are limited to 25 MB.");
        }

        return file;
    }
}
