using Robust.Shared.GameStates;

namespace Content.Shared.War;

/// <summary>Mode-owned body policy; never applied to ordinary station characters.</summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class FrontlinePlayerComponent : Component;
