using Zigote.Core.State;

namespace Zigote.Preferences;

/// <summary>
///     The non-generic face of <see cref="Preference{T}" /> — what a settings UI or a group reset
///     needs without knowing the value type: identity, whether a persisted value backs it, and the
///     way back to the default. Being an <see cref="ISignal" />, it can also be observed generically
///     (<c>pref.Observe(...)</c>) to refresh a row when the value changes.
/// </summary>
public interface IPreference : ISignal
{
    string Key { get; }

    /// <summary>True when a persisted value backs the current one; false means the default is live.</summary>
    bool IsSet { get; }

    /// <summary>The <c>T</c> of the underlying <see cref="Preference{T}" />.</summary>
    Type ValueType { get; }

    /// <summary>Back to the default; removes the persisted entry so the next load is unset too.</summary>
    void Reset();
}