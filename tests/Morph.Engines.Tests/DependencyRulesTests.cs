using System.Reflection;

namespace Morph.Engines.Tests;

public sealed class DependencyRulesTests
{
    [Fact]
    public void Engines_reference_only_Core_among_Morph_assemblies()
    {
        var engines = Assembly.Load("Morph.Engines");

        var morphReferences = engines.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null && n.StartsWith("Morph.", StringComparison.Ordinal));

        Assert.All(morphReferences, n => Assert.Equal("Morph.Core", n));
    }
}
