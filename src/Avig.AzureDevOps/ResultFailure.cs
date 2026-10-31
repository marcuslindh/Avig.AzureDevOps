namespace Avig.AzureDevOps;

/// <summary>
/// Passes a failure on from a call of one type to a method that answers with another.
/// </summary>
/// <remarks>
/// A call that fails should end the method with the same message and status, so that the user
/// sees what actually went wrong. <c>return response.WithoutValue().AsFailure&lt;Summary&gt;();</c>
/// does that in one line instead of four.
/// </remarks>
internal static class ResultFailure
{
    /// <summary>The message when a failure carries none, which Avig.Result does not prevent.</summary>
    private const string UnknownError = "Azure DevOps could not be reached for an unknown reason.";

    /// <summary>The same outcome without the value: success stays success, a failure keeps its message and status.</summary>
    /// <typeparam name="T">The value the result carries.</typeparam>
    /// <param name="result">The result to drop the value from.</param>
    /// <returns>A result without a value, with the same status and message.</returns>
    public static Result WithoutValue<T>(this Result<T> result)
    {
        if (result.IsFailure(out string? error, out ResultStatus status))
        {
            return Result.Failure(error, status);
        }

        return Result.Success();
    }

    /// <summary>A failure as the result of a method that answers with a value.</summary>
    /// <typeparam name="T">The value the method would have answered with.</typeparam>
    /// <param name="failed">The failure, with its message and status.</param>
    /// <returns>A failed result with the same message and status.</returns>
    public static Result<T> AsFailure<T>(this Result failed) =>
        Result<T>.Failure(failed.Error ?? UnknownError, failed.Status);
}
