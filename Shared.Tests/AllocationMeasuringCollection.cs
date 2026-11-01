using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>
/// The tests that measure what a renderer's frame allocates run alone, after the parallel ones. The button glyphs'
/// sprites are shared (<c>ButtonGlyph</c>) and every <c>ThemeRenderer.Dispose</c> empties them, so a renderer disposed by a
/// test beside a measurement makes its next frame render them anew: 40 bytes a sprite, in frames that depend on the timing.
/// The app has one renderer, so this only happens in a test run.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationMeasuringCollection
{
    public const string Name = "Allocation-measuring";
}
