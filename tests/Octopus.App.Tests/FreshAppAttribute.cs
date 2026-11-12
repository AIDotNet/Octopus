using System.Reflection;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

namespace Octopus.App.Tests;

/// <summary>
/// Applied to the whole assembly: every test starts on a new container with empty in-memory stores and its own data
/// folder, and the container is disposed after it. No test needs a reset of its own and none depends on another.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
public sealed class FreshAppAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest, IXunitTest test) => TestApp.Fresh();

    public override void After(MethodInfo methodUnderTest, IXunitTest test) => TestApp.Dispose();
}
