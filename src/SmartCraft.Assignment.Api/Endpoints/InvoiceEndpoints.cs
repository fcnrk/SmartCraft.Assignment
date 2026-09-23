using SmartCraft.Assignment.Api.Application;
using SmartCraft.Assignment.Api.Authorization;
using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Api.Endpoints;

public sealed record InvoiceLineResponse(
    Guid WorklogId, Guid WorkerId, DateOnly WorkDate, string WorkerRole, decimal HourlyRate,
    decimal NormalHours, decimal OvertimeHours, decimal OvertimeMultiplier, decimal NormalAmount, decimal OvertimeAmount, decimal LineTotal)
{
    public static InvoiceLineResponse From(InvoiceLine l) => new(
        l.WorklogId, l.WorkerId, l.WorkDate, l.WorkerRole, l.HourlyRate,
        l.NormalHours, l.OvertimeHours, l.OvertimeMultiplier, l.NormalAmount, l.OvertimeAmount, l.LineTotal);
}

public sealed record InvoiceResponse(Guid Id, Guid ProjectId, DateTimeOffset CreatedAt, decimal Total, IReadOnlyList<InvoiceLineResponse> Lines)
{
    public static InvoiceResponse From(Invoice i) =>
        new(i.Id, i.ProjectId, i.CreatedAt, i.Total, i.Lines.Select(InvoiceLineResponse.From).ToList());
}

/// <summary>docs/03-api-transactions.md "Invoices" + docs/04 "Idempotency": creation requires
/// an <c>Idempotency-Key</c> header, validated here (missing/blank/too long -> 400) before the
/// request reaches <see cref="InvoiceService"/>.</summary>
public static class InvoiceEndpoints
{
    private const int MaxIdempotencyKeyLength = 200;

    public static void MapInvoiceEndpoints(this WebApplication app)
    {
        app.MapPost("/api/projects/{projectId:guid}/invoices", async (
                Guid projectId, HttpRequest request, InvoiceService service, CancellationToken ct) =>
            {
                if (!TryGetIdempotencyKey(request, out var key, out var error))
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Bad Request", detail: error);
                }

                var result = await service.CreateInvoiceAsync(projectId, key, ct);
                var response = InvoiceResponse.From(result.Invoice);
                return result.IsReplay
                    ? Results.Ok(response)
                    : Results.Created($"/api/invoices/{result.Invoice.Id}", response);
            })
            .RequireAuthorization(Permissions.InvoiceCreate);

        app.MapGet("/api/invoices/{id:guid}", async (Guid id, InvoiceService service, CancellationToken ct) =>
                Results.Ok(InvoiceResponse.From(await service.GetInvoiceAsync(id, ct))))
            .RequireAuthorization(Permissions.InvoiceRead);
    }

    private static bool TryGetIdempotencyKey(HttpRequest request, out string key, out string? error)
    {
        key = string.Empty;
        error = null;

        if (!request.Headers.TryGetValue("Idempotency-Key", out var values) || string.IsNullOrWhiteSpace(values.ToString()))
        {
            error = "The Idempotency-Key header is required.";
            return false;
        }

        var trimmed = values.ToString().Trim();
        if (trimmed.Length > MaxIdempotencyKeyLength)
        {
            error = $"The Idempotency-Key header must be at most {MaxIdempotencyKeyLength} characters.";
            return false;
        }

        key = trimmed;
        return true;
    }
}
