namespace CaseAgents.Api;

/// <summary>Error body of every 4xx/5xx the API writes itself. <c>code</c> is set on 409 responses.</summary>
public sealed record ApiError(string Error, string? Code = null);

/// <summary>Values of <see cref="ApiError.Code"/> on 409 responses.</summary>
public static class ConflictCodes
{
    public const string VersionChanged = "version_changed";
    public const string AiLocked = "ai_locked";
    public const string FinalAnswerExists = "final_answer_exists";
    public const string Touched = "touched";
    public const string NotApplicable = "not_applicable";
    public const string CaseNotReady = "case_not_ready";
    public const string Conflict = "conflict";
}

internal static class ApiResults
{
    public static IResult BadRequest(string message) => Results.BadRequest(new ApiError(message));

    public static IResult Conflict(string code, string message) => Results.Conflict(new ApiError(message, code));

    public static IResult VersionChanged() =>
        Conflict(ConflictCodes.VersionChanged, "The case changed since it was read; read it again and retry.");

    public static IResult PreconditionRequired() =>
        Results.Json(new ApiError("The If-Match header with the case ETag is required."), statusCode: StatusCodes.Status428PreconditionRequired);

    public static IResult Unprocessable(string message) => Results.UnprocessableEntity(new ApiError(message));
}

internal static class HttpExtensions
{
    /// <summary>The raw If-Match value (the case ETag as returned, quotes included), or null when absent.</summary>
    public static string? IfMatch(this HttpRequest request) => NonEmpty(request.Headers.IfMatch.ToString());

    public static string? IdempotencyKey(this HttpRequest request) => NonEmpty(request.Headers["Idempotency-Key"].ToString());

    public static void SetETag(this HttpResponse response, string? etag)
    {
        if (!string.IsNullOrEmpty(etag))
            response.Headers.ETag = etag;
    }

    private static string? NonEmpty(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
