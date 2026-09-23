using Microsoft.AspNetCore.Diagnostics;
using SmartCraft.Assignment.Api.Domain;

namespace SmartCraft.Assignment.Api.Endpoints;

/// <summary>
/// Maps <see cref="DomainException"/> to ProblemDetails (docs/03-api-transactions.md "HTTP
/// behavior"). Anything else (e.g. InvalidOperationException from InvoiceService's calculator-
/// output validation) is intentionally left unhandled here and falls through to the default
/// ProblemDetails 500 response registered via <c>AddProblemDetails()</c> in Program.cs — those
/// are server bugs, not domain errors with a more specific status.
/// </summary>
public sealed class DomainExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DomainException domainException)
        {
            return false;
        }

        var status = domainException.Kind switch
        {
            DomainErrorKind.Validation => StatusCodes.Status400BadRequest,
            DomainErrorKind.NotFound => StatusCodes.Status404NotFound,
            DomainErrorKind.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError,
        };

        // Results.Problem sets application/problem+json, same as the endpoints' own 400/403s.
        await Results.Problem(statusCode: status, title: domainException.Kind.ToString(), detail: domainException.Message)
            .ExecuteAsync(httpContext);

        return true;
    }
}
