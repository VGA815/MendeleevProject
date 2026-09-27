namespace Mendeleev.SharedKernel
{
    public sealed record ValidationError : Error
    {
        public ValidationError(Error[] errors)
            : base(
                  "Validation.General",
                  errors.Length > 0 ? errors[0].Description : "Некорректные данные",
                  ErrorType.Validation)
        {
            Errors = errors;
        }

        public Error[] Errors { get; }

        public static ValidationError FromResults(IEnumerable<Result> results) =>
            new(results.Where(r => r.IsFailure).Select(r => r.Error).ToArray());
    }
}
