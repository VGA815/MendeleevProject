using Mendeleev.Application.Panel.NodeGuard;

namespace Mendeleev.UnitTests.Application
{
    /// <summary>FR-NODE-18: the parts of the node guard that need no panel and no network.</summary>
    public class NodeGuardTests
    {
        [Theory]
        [InlineData("ya.ru:443", "ya.ru", 443)]
        [InlineData("vk.com", "vk.com", 443)]
        [InlineData("198.51.100.7:8443", "198.51.100.7", 8443)]
        [InlineData("example.test:port", "example.test:port", 443)]
        public void ControlTarget_IsHostAndPort_Defaulting443(string target, string host, int port) =>
            CheckNodesCommandHandler.ParseTarget(target).ShouldBe((host, port));

        [Fact]
        public void Failures_CountInARow_AndARecoveryIsNewsOnlyAfterTheNodeWasReported()
        {
            var state = new NodeGuardState();

            state.Fail("NL1").ShouldBe(1);
            state.Fail("nl1").ShouldBe(2);
            state.IsHandled("NL1").ShouldBeFalse();
            state.Recover("NL1").ShouldBeFalse();
            state.Failures("NL1").ShouldBe(0);

            state.Fail("NL1");
            state.Fail("NL1");
            state.MarkHandled("NL1");
            state.IsHandled("NL1").ShouldBeTrue();
            state.Recover("NL1").ShouldBeTrue();
            state.IsHandled("NL1").ShouldBeFalse();
        }

        [Fact]
        public void NodesThatLeftTheActiveSet_AreForgotten()
        {
            var state = new NodeGuardState();
            state.Fail("NL1");
            state.Fail("DE1");
            state.MarkHandled("DE1");

            state.Keep(["nl1"]);

            state.Failures("NL1").ShouldBe(1);
            state.Failures("DE1").ShouldBe(0);
            state.IsHandled("DE1").ShouldBeFalse();
        }
    }
}
