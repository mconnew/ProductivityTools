using System;

namespace EdgeProfileRouter.Edge;

/// <summary>
/// One Microsoft Edge profile as it appears in Edge's <c>Local State</c> file.
///
/// <para><see cref="Directory"/> is the on-disk folder name (e.g. <c>Default</c>,
/// <c>Profile 1</c>) — this is the stable identifier passed to Edge as
/// <c>--profile-directory</c> and the value stored in routing rules. The friendly
/// <see cref="Name"/> and <see cref="UserName"/> can be renamed or re-shuffled by the user,
/// so they are used for display only, never as a key.</para>
/// </summary>
public sealed record EdgeProfile(string Directory, string Name, string UserName)
{
    /// <summary>A human-friendly one-line label, e.g. "Profile 2 — matt@outlook.com (Profile 1)".</summary>
    public string DisplayLabel
    {
        get
        {
            string primary = string.IsNullOrWhiteSpace(Name) ? Directory : Name;
            string label = primary;
            if (!string.IsNullOrWhiteSpace(UserName))
                label += " — " + UserName;
            if (!string.Equals(primary, Directory, StringComparison.Ordinal))
                label += "  (" + Directory + ")";
            return label;
        }
    }
}
