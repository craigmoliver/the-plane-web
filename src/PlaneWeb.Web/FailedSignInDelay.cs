namespace PlaneWeb.Web;

/// <summary>
/// Makes every failed sign-in take at least <see cref="Minimum"/>, measured from the start of the attempt.
/// The paths differ in more than password hashing: a wrong password on a real account also writes the
/// failed-attempt count to the database, which an unknown email doesn't. Waiting out the remainder hides
/// all of that (on SQLite or a remote Postgres) as long as the slowest path stays under the minimum; when one
/// doesn't, a warning is logged so the setting can be raised.
/// </summary>
public sealed class FailedSignInDelay(IConfiguration config, TimeProvider time, ILogger<FailedSignInDelay> log)
{
    public const string ConfigKey = "PlaneWeb:Auth:FailedSignInMinMs";

    public TimeSpan Minimum { get; } = TimeSpan.FromMilliseconds(Math.Clamp(config.GetValue(ConfigKey, 1000), 0, 10_000));

    /// <summary>A start mark for <see cref="WaitAsync"/>.</summary>
    public long Start() => time.GetTimestamp();

    /// <summary>Waits until <see cref="Minimum"/> has passed since <paramref name="started"/>.</summary>
    public async Task WaitAsync(long started)
    {
        var elapsed = time.GetElapsedTime(started);
        if (elapsed < Minimum)
            await Task.Delay(Minimum - elapsed, time);
        else if (Minimum > TimeSpan.Zero)
            log.LogWarning("A failed sign-in took {Elapsed} ms, more than the {Minimum} ms minimum ({Key}); response times may reveal which emails have accounts",
                (int)elapsed.TotalMilliseconds, (int)Minimum.TotalMilliseconds, ConfigKey);
    }
}
