namespace Creuser.Persistence.AppSettings;

/// <summary>
/// Typed key/value store backed by the <c>cr.app_settings</c> table.
/// Each well-known key (<c>branding</c>, <c>smtp</c>, <c>ai-providers</c>, ...)
/// stores a JSON-serialized record. Values are read-mostly singleton config —
/// not event-sourced state.
/// </summary>
public interface IAppSettingsStore
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        where T : class;

    Task SetAsync<T>(string key, T value, Guid? updatedBy, CancellationToken ct = default)
        where T : class;
}
