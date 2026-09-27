namespace Mendeleev.SharedKernel
{
    /// <summary>
    /// An expected failure. <see cref="Description"/> is written for the person on the other side —
    /// the bot and the web cabinet show it as is, so it is in Russian.
    /// </summary>
    public record Error
    {
        public static readonly Error None = new(string.Empty, string.Empty, ErrorType.Failure);
        public static readonly Error NullValue = new(
            "General.Null",
            "Значение не задано",
            ErrorType.Failure);

        public Error(string code, string description, ErrorType type)
        {
            Code = code;
            Description = description;
            Type = type;
        }

        public string Code { get; }
        public string Description { get; }
        public ErrorType Type { get; }

        public static Error Failure(string code, string description) =>
            new(code, description, ErrorType.Failure);
        public static Error NotFound(string code, string description) =>
            new(code, description, ErrorType.NotFound);
        public static Error Problem(string code, string description) =>
            new(code, description, ErrorType.Problem);
        public static Error Conflict(string code, string description) =>
            new(code, description, ErrorType.Conflict);
        public static Error Forbidden(string code, string description) =>
            new(code, description, ErrorType.Forbidden);
        public static Error Unauthorized(string code, string description) =>
            new(code, description, ErrorType.Unauthorized);
        public static Error Validation(string code, string description) =>
            new(code, description, ErrorType.Validation);
        public static Error ServiceUnavailable(string code, string description) =>
            new(code, description, ErrorType.ServiceUnavailable);
        public static Error TooManyRequests(string code, string description) =>
            new(code, description, ErrorType.TooManyRequests);
    }
}
