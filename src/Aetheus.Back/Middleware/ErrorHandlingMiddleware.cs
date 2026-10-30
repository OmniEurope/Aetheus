// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;

namespace Aetheus.Back.Middleware;

public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (NotFoundException ex)
        {
            await WriteErrorAsync(context, HttpStatusCode.NotFound, ex.Message);
        }
        catch (BadRequestException ex)
        {
            await WriteErrorAsync(context, HttpStatusCode.BadRequest, ex.Message, ex.Problems);
        }
        catch (ConflictException ex)
        {
            await WriteErrorAsync(context, HttpStatusCode.Conflict, ex.Message, code: ex.Code);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = 499; // Client Closed Request
        }
        catch (BadHttpRequestException ex)
        {
            // Kestrel uses BadHttpRequestException for protocol failures such as an exceeded
            // endpoint body limit. Preserve its explicit 4xx status instead of hiding it as 500.
            await WriteErrorAsync(context, (HttpStatusCode)ex.StatusCode, ex.Message);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
        {
            SqlState: Npgsql.PostgresErrorCodes.UniqueViolation or Npgsql.PostgresErrorCodes.SerializationFailure
        })
        {
            // RBAC audit, finding 10: two requests writing the same row at once lose a race, not the
            // server. The later one gets a 409 it can retry; the constraint name stays in the log only.
            logger.LogWarning(ex, "Concurrent write conflict for {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteErrorAsync(context, HttpStatusCode.Conflict,
                "This change conflicts with another one made at the same time; reload and try again.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteErrorAsync(context, HttpStatusCode.InternalServerError, "An unexpected error occurred.");
        }
    }

    /// <summary>Retain the refusal reason for RequestErrorLoggingMiddleware so request logs explain
    /// the status.</summary>
    public const string ErrorReasonItem = "Aetheus.ErrorReason";

    private static async Task WriteErrorAsync(
        HttpContext context, HttpStatusCode statusCode, string message, IReadOnlyList<ProblemDto>? problems = null,
        string? code = null)
    {
        context.Items[ErrorReasonItem] = message;
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "application/json";
        var error = new ApiError
        {
            Message = message,
            CorrelationId = context.TraceIdentifier,
            Code = code,
            Problems = problems is null ? null : [.. problems]
        };
        await context.Response.WriteAsync(JsonSerializer.Serialize(error, JsonOptions));
    }
}
