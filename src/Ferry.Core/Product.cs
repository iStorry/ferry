using System.Reflection;

namespace Ferry.Core;

/// <summary>Names and wording shared by every front end.</summary>
public static class Product
{
    public const string Name = "Ferry";

    /// <summary>The running version (major.minor.patch).</summary>
    public static string Version => typeof(Product).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// The exact build ("0.1.0+{commit}").
    /// </summary>
    public static string Build =>
        typeof(Product).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? Version;

    /// <summary>A build for people: "1.0.0+b0f6d6255ce…" → "1.0.0 (b0f6d62)"; a plain version as it is.</summary>
    public static string Describe(string build) =>
        build.IndexOf('+', StringComparison.Ordinal) is var plus and >= 0
            ? $"{build[..plus]} ({build[(plus + 1)..][..Math.Min(7, build.Length - plus - 1)]})"
            : build;
}
