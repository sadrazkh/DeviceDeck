namespace DeviceDeck.Core.Backups.Contacts;

/// <param name="Id">Stable within one backup (the address book's row id).</param>
/// <param name="DisplayName">What a phone would show: the name, else the company, else the first number or address.</param>
/// <param name="Birthday">"yyyy-MM-dd", or "--MM-dd" when the year is unknown.</param>
public sealed record Contact(
    string Id,
    string DisplayName,
    string? FirstName,
    string? MiddleName,
    string? LastName,
    string? Prefix,
    string? Suffix,
    string? Nickname,
    string? Organization,
    string? Department,
    string? JobTitle,
    string? Note,
    string? Birthday,
    IReadOnlyList<LabeledValue> Phones,
    IReadOnlyList<LabeledValue> Emails,
    IReadOnlyList<LabeledValue> Urls,
    IReadOnlyList<PostalAddress> Addresses,
    bool HasPhoto,
    DateTimeOffset? Modified);

/// <param name="Label">A known key (mobile, iphone, home, work, main, homeFax, workFax, otherFax, pager, other, school, homePage) or the user's own label.</param>
public sealed record LabeledValue(string Label, string Value);

public sealed record PostalAddress(string Label, string? Street, string? City, string? Region, string? PostalCode, string? Country);
