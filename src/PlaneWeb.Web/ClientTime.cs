using Microsoft.JSInterop;

namespace PlaneWeb.Web;

/// <summary>
/// The viewer's time zone, read once per connection from the browser. Until it is known (e.g. during
/// prerendering, before JS interop is available) times are shown in the server's zone.
/// </summary>
public sealed class ClientTime(IJSRuntime js)
{
    private Task? _resolve;

    public TimeZoneInfo Zone { get; private set; } = TimeZoneInfo.Local;

    /// <summary>True once the browser's zone was asked for (whether or not it could be used).</summary>
    public bool IsResolved { get; private set; }

    /// <summary>Asks the browser for its zone once; later calls share the first one. Call after the first render.</summary>
    public Task EnsureAsync() => _resolve ??= ResolveAsync();

    private async Task ResolveAsync()
    {
        try
        {
            if (await js.InvokeAsync<string?>("planeWebTimeZone") is { Length: > 0 } id && TryFind(id) is { } zone)
                Zone = zone;
        }
        catch (JSException) { }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) { } // no interactive connection (static rendering)
        IsResolved = true;
    }

    /// <summary>The zone for an IANA id from the browser, or null if the server doesn't know it.</summary>
    public static TimeZoneInfo? TryFind(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }

    public DateTimeOffset ToLocal(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, Zone);

    public string Format(DateTimeOffset t, string format) => ToLocal(t).ToString(format);
}
