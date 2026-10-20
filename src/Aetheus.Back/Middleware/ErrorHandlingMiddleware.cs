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
            await WriteErrorAsync(context, HttpStatusCode.BadRequest, ex.Message);
        }
        catch (ConflictException ex)
        {
            await WriteErrorAsync(context, HttpStatusCode.Conflict, ex.Message);
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
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteErrorAsync(context, HttpStatusCode.InternalServerError, "An unexpected error occurred.");
        }
    }

    /// <summary>Retain the refusal reason for RequestErrorLoggingMiddleware so request logs explain
    /// the status.</summary>
    public const string ErrorReasonItem = "Aetheus.ErrorReason";

    private static async Task WriteErrorAsync(HttpContext context, HttpStatusCode statusCode, string message)
    {
        context.Items[ErrorReasonItem] = message;
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "application/json";
        var error = new ApiError
        {
            Message = message,
            CorrelationId = context.TraceIdentifier
        };
        await context.Response.WriteAsync(JsonSerializer.Serialize(error, JsonOptions));
    }
}
