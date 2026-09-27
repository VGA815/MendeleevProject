using Mendeleev.Application.Abstractions.Messaging;

namespace Mendeleev.Application.Accounts.IssueAccountKey
{
    /// <summary>
    /// Issues or reissues the key for signing in on the site (FR-ACC-04). The key is returned once, in
    /// display form; only its HMAC is stored, and the previous key stops working.
    /// </summary>
    public sealed record IssueAccountKeyCommand(long UserId) : ICommand<string>;
}
