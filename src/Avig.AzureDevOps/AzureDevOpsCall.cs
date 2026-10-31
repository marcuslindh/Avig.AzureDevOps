using System.Net;
using System.Text.Json;

namespace Avig.AzureDevOps;

/// <summary>
/// The boundary of the public API: whatever goes wrong inside a call comes out as a
/// <see cref="Result"/> with a message that can be shown as it is.
/// </summary>
/// <remarks>
/// <para>
/// Inside the package a failure travels as <see cref="AzureDevOpsException"/>, because a
/// call often takes several requests and the first that fails should end it. Nothing of that
/// reaches the caller. Every public method on the clients goes through here, and the status
/// says what kind of failure it was:
/// </para>
/// <list type="bullet">
/// <item><see cref="ResultStatus.Unauthorized"/> — no token, or the server rejected it (401).</item>
/// <item><see cref="ResultStatus.Forbidden"/> — the token lacks a permission (403).</item>
/// <item><see cref="ResultStatus.NotFound"/> — the item does not exist (404).</item>
/// <item><see cref="ResultStatus.Conflict"/> — the server refused because of the state, for example a policy (409).</item>
/// <item><see cref="ResultStatus.Invalid"/> — the request was wrong: a missing argument, or 400 from the server.</item>
/// <item><see cref="ResultStatus.Unavailable"/> — the network, a timeout, throttling or the server being down (429, 5xx).</item>
/// <item><see cref="ResultStatus.Error"/> — anything else the server said.</item>
/// </list>
/// <para>
/// Cancellation through the caller's own <see cref="CancellationToken"/> is not a failure
/// and is not turned into one: the <see cref="OperationCanceledException"/> passes through,
/// as everywhere else in .NET, so that whoever cancelled can tell it apart from an error.
/// </para>
/// </remarks>
internal static class AzureDevOpsCall
{
    /// <summary>A call that returns a value.</summary>
    public static async Task<Result<T>> RunAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCallFailure(exception, cancellationToken))
        {
            CallFailure failure = Describe(exception);

            return Result<T>.Failure(failure.Message, failure.Status);
        }
    }

    /// <summary>A call that already answers with a result, for example "not found".</summary>
    public static async Task<Result<T>> RunResultAsync<T>(Func<Task<Result<T>>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCallFailure(exception, cancellationToken))
        {
            CallFailure failure = Describe(exception);

            return Result<T>.Failure(failure.Message, failure.Status);
        }
    }

    /// <summary>A call that only does something.</summary>
    public static async Task<Result> RunVoidAsync(Func<Task> call, CancellationToken cancellationToken)
    {
        try
        {
            await call().ConfigureAwait(false);

            return Result.Success();
        }
        catch (Exception exception) when (IsCallFailure(exception, cancellationToken))
        {
            CallFailure failure = Describe(exception);

            return Result.Failure(failure.Message, failure.Status);
        }
    }

    /// <summary>
    /// Whether an exception is a failure of the call. The caller's own cancellation is not,
    /// and neither is a bug — a <see cref="NullReferenceException"/> should be seen, not
    /// dressed up as a message from Azure DevOps.
    /// </summary>
    private static bool IsCallFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception is AzureDevOpsException
            or TaskCanceledException
            or HttpRequestException
            or JsonException
            or ArgumentException;
    }

    /// <summary>The message and status for an exception that <see cref="IsCallFailure"/> accepted.</summary>
    private static CallFailure Describe(Exception exception) => exception switch
    {
        AzureDevOpsException azure => new CallFailure(azure.Message, Classify(azure)),
        TaskCanceledException => new CallFailure("Azure DevOps did not respond in time.", ResultStatus.Unavailable),
        HttpRequestException network => new CallFailure($"Could not reach Azure DevOps: {network.Message}", ResultStatus.Unavailable),
        JsonException => new CallFailure("Azure DevOps sent a response that could not be read.", ResultStatus.Error),
        _ => new CallFailure(exception.Message, ResultStatus.Invalid),
    };

    private static ResultStatus Classify(AzureDevOpsException exception)
    {
        if (exception.IsNotFound)
        {
            return ResultStatus.NotFound;
        }

        return exception.StatusCode switch
        {
            HttpStatusCode.Unauthorized => ResultStatus.Unauthorized,
            HttpStatusCode.Forbidden => ResultStatus.Forbidden,
            HttpStatusCode.NotFound => ResultStatus.NotFound,
            HttpStatusCode.Conflict => ResultStatus.Conflict,
            HttpStatusCode.BadRequest => ResultStatus.Invalid,
            HttpStatusCode.TooManyRequests => ResultStatus.Unavailable,
            >= HttpStatusCode.InternalServerError => ResultStatus.Unavailable,
            _ => ResultStatus.Error,
        };
    }

    private sealed record CallFailure(string Message, ResultStatus Status);
}
