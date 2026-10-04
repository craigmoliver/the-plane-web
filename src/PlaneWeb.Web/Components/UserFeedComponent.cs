using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using PlaneWeb.Core;
using PlaneWeb.Infrastructure.Services;

namespace PlaneWeb.Web.Components;

/// <summary>
/// Base for pages that show the signed-in user's live data: loads their settings, holds a lease on the
/// poller for their area, and follows changes to either. Other users' settings changes are ignored.
/// </summary>
[Microsoft.AspNetCore.Authorization.Authorize] // also checked during interactive navigation and revalidation
public abstract class UserFeedComponent : ComponentBase, IAsyncDisposable
{
    [Inject] protected SettingsService SettingsSvc { get; set; } = default!;
    [Inject] private PollerRegistry Pollers { get; set; } = default!;
    [CascadingParameter] private Task<AuthenticationState> AuthState { get; set; } = default!;

    protected string? UserId { get; private set; }
    protected WallSettings? Settings { get; set; }
    protected WallSnapshot Snapshot { get; private set; } = WallSnapshot.Empty;
    private PollerLease? _lease;
    private bool _disposed;
    /// <summary>True once the page has been disposed (it may happen while initialization is still awaiting).</summary>
    protected bool IsDisposed => _disposed;

    protected override async Task OnInitializedAsync()
    {
        UserId = (await AuthState).User.UserId();
        Settings = await SettingsSvc.GetAsync(UserId);
        if (_disposed) return; // navigated away while loading: don't take a lease nobody will release
        Attach(Settings);
        SettingsSvc.Changed += OnSettingsEvent;
    }

    /// <summary>Called on the renderer's sync context after a new snapshot arrives.</summary>
    protected virtual Task OnSnapshotAsync() => Task.CompletedTask;

    /// <summary>Called after this user's saved settings change (the lease is already moved).</summary>
    protected virtual Task OnUserSettingsChangedAsync() => Task.CompletedTask;

    private void Attach(WallSettings s)
    {
        var old = _lease;
        _lease = Pollers.Acquire(s);
        if (old?.Store != _lease.Store)
        {
            if (old is not null) old.Store.Updated -= OnSnapshotEvent;
            _lease.Store.Updated += OnSnapshotEvent;
        }
        old?.Dispose();
        Snapshot = _lease.Store.Current;
    }

    private void OnSnapshotEvent(WallSnapshot ignored) => _ = InvokeAsync(async () =>
    {
        // Read the current lease rather than the captured snapshot: a callback queued by the previous
        // area's poller must not overwrite the new area's data.
        if (_disposed || _lease is null) return;
        Snapshot = _lease.Store.Current;
        await OnSnapshotAsync();
        StateHasChanged();
    });

    private void OnSettingsEvent(string? userId, WallSettings next)
    {
        if (userId != UserId) return;
        _ = InvokeAsync(async () =>
        {
            if (_disposed) return;
            Settings = next;
            Attach(next);
            await OnUserSettingsChangedAsync();
            StateHasChanged();
        });
    }

    public virtual ValueTask DisposeAsync()
    {
        _disposed = true;
        SettingsSvc.Changed -= OnSettingsEvent;
        if (_lease is not null)
        {
            _lease.Store.Updated -= OnSnapshotEvent;
            _lease.Dispose();
        }
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
