using System.Reflection;

namespace Morph.Core.Tests;

public sealed class DependencyRulesTests
{
    [Fact]
    public void Core_does_not_reference_any_UI_framework()
    {
        var core = Assembly.Load("Morph.Core");

        var uiReferences = core.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is "PresentationFramework" or "PresentationCore" or "WindowsBase" or "System.Windows.Forms");

        Assert.Empty(uiReferences);
    }
}
