using System.Reflection;
using Mendeleev.Application.Abstractions.Messaging;
using Mendeleev.Domain.Users;
using Mendeleev.Infrastructure.Database;
using Mendeleev.SharedKernel;
using NetArchTest.Rules;

namespace Mendeleev.ArchitectureTests
{
    /// <summary>
    /// Layer boundaries, as in DevStart: Web → Application → Domain; Infrastructure implements the
    /// Application's ports. The panel and the aggregator stay behind interfaces (FR-PNL-01, FR-PAY-08),
    /// so their SDKs and HTTP details never reach the business logic.
    /// </summary>
    public class LayerTests
    {
        private static readonly Assembly SharedKernel = typeof(Entity).Assembly;
        private static readonly Assembly Domain = typeof(User).Assembly;
        private static readonly Assembly Application = typeof(ICommand).Assembly;
        private static readonly Assembly Infrastructure = typeof(ApplicationDbContext).Assembly;
        private static readonly Assembly Web = typeof(Mendeleev.Web.Program).Assembly;

        [Fact]
        public void SharedKernel_DependsOnNothingOfOurs()
        {
            Types.InAssembly(SharedKernel).Should()
                .NotHaveDependencyOnAny("Mendeleev.Domain", "Mendeleev.Application", "Mendeleev.Infrastructure", "Mendeleev.Web")
                .GetResult().IsSuccessful.ShouldBeTrue();
        }

        [Fact]
        public void Domain_DependsOnlyOnSharedKernel()
        {
            Types.InAssembly(Domain).Should()
                .NotHaveDependencyOnAny("Mendeleev.Application", "Mendeleev.Infrastructure", "Mendeleev.Web", "Microsoft.EntityFrameworkCore", "Telegram.Bot", "Npgsql")
                .GetResult().IsSuccessful.ShouldBeTrue();
        }

        [Fact]
        public void Application_DoesNotKnowInfrastructureOrPresentation()
        {
            TestResult result = Types.InAssembly(Application).Should()
                .NotHaveDependencyOnAny("Mendeleev.Infrastructure", "Mendeleev.Web", "Telegram.Bot", "Npgsql", "Quartz", "Microsoft.AspNetCore")
                .GetResult();

            result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
        }

        [Fact]
        public void Infrastructure_DoesNotKnowPresentation()
        {
            Types.InAssembly(Infrastructure).Should()
                .NotHaveDependencyOn("Mendeleev.Web")
                .GetResult().IsSuccessful.ShouldBeTrue();
        }

        [Fact]
        public void Remnawave_IsOnlyUsedInsideInfrastructurePanel()
        {
            Types.InAssemblies([Application, Web]).Should()
                .NotHaveDependencyOn("Mendeleev.Infrastructure.Panel")
                .GetResult().IsSuccessful.ShouldBeTrue();
        }

        [Fact]
        public void Handlers_AreNotPublic()
        {
            TestResult result = Types.InAssembly(Application)
                .That().HaveNameEndingWith("CommandHandler").Or().HaveNameEndingWith("QueryHandler")
                .Should().NotBePublic()
                .GetResult();

            result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
        }
    }
}
