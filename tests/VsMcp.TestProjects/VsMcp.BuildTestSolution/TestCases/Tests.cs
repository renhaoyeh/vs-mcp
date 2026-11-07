using Xunit;
namespace ManagedFixture
{
    public class Arithmetic
    {
        [Fact] public void Passes() { Assert.Equal(4, 2 + 2); }
        [Fact] public void Fails() { Assert.True(false, "Intentional failure for MCP result validation"); }
        [Fact] public void WaitForCancel() { System.Threading.Thread.Sleep(30000); }
    }
}
