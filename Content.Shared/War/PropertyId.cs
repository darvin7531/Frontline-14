using System;
using Robust.Shared.Serialization;

namespace Content.Shared.War;

[Serializable, NetSerializable]
public readonly record struct PropertyId(string Id);
