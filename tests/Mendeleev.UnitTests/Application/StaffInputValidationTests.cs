using Mendeleev.Application.Admin.Users;
using Mendeleev.Domain.Staff;

namespace Mendeleev.UnitTests.Application
{
    /// <summary>Staff see validation messages as they are, so each one must be a single Russian sentence.</summary>
    public class StaffInputValidationTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("ок")]
        public void ShortReason_GetsOneRussianMessage(string reason)
        {
            new CompensateCommandValidator().Validate(new CompensateCommand(1, 2, 3, reason))
                .Errors.ShouldHaveSingleItem().ErrorMessage.ShouldBe(StaffErrors.ReasonRequired.Description);
            new BlockUserCommandValidator().Validate(new BlockUserCommand(1, 2, reason))
                .Errors.ShouldHaveSingleItem().ErrorMessage.ShouldBe(StaffErrors.ReasonRequired.Description);
        }

        [Theory]
        [InlineData(300, "перевод на карту", true)]
        [InlineData(0, "перевод на карту", false)]
        [InlineData(199.5, "перевод на карту", false)]
        [InlineData(100_001, "перевод на карту", false)]
        [InlineData(300, "ок", false)]
        [InlineData(300, "   ", false)]
        public void ManualPayment_NeedsWholeRublesAndAComment(double amount, string comment, bool valid)
        {
            var result = new RecordManualPaymentCommandValidator().Validate(
                new RecordManualPaymentCommand(1, 2, "basic_1m", (decimal)amount, comment));

            result.IsValid.ShouldBe(valid);
            if (!valid)
            {
                result.Errors.ShouldHaveSingleItem().ErrorMessage.ShouldNotContain("'");
            }
        }
    }
}
