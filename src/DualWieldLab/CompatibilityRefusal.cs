using System;

namespace DualWieldLab;

// A pistol or model this build cannot dual-wield. The player is told once per raid, unlike transient refusals.
internal sealed class CompatibilityRefusal : InvalidOperationException
{
    internal CompatibilityRefusal(string message) : base(message) { }
}
