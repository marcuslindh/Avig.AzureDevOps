namespace Avig.AzureDevOps.Tests;

/// <summary>
/// The clients answer with results. A test that is not about a failure wants the value, and a
/// failure should then fail the test with the client's own message.
/// </summary>
internal static class TestResults
{
    public static T Ok<T>(this Result<T> result)
    {
        Assert.True(result.IsSuccess(out T? data), result.Error);

        return data;
    }

    public static async Task<T> Ok<T>(this Task<Result<T>> pending) => (await pending).Ok();

    public static async Task Ok(this Task<Result> pending)
    {
        Result result = await pending;

        Assert.True(result.IsSuccess(out string? error, out _), error);
    }

    /// <summary>The failure's message and status; fails the test if the call succeeded.</summary>
    public static (string Error, ResultStatus Status) Failed<T>(this Result<T> result)
    {
        Assert.True(result.IsFailure(out string? error, out ResultStatus status), "The call should have failed.");

        return (error, status);
    }

    public static async Task<(string Error, ResultStatus Status)> Failed<T>(this Task<Result<T>> pending) =>
        (await pending).Failed();

    public static async Task<(string Error, ResultStatus Status)> Failed(this Task<Result> pending)
    {
        Result result = await pending;

        Assert.True(result.IsFailure(out string? error, out ResultStatus status), "The call should have failed.");

        return (error, status);
    }
}
