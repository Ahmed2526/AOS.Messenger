using AOS.Messenger.Domain.Common;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Domain.Entities;

/// <summary>
/// Represents a built-in starter pack or a user-owned custom sticker pack.
/// </summary>
public sealed class StickerPack : Entity<StickerPackId>
{
    public string? Slug { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string AuthorName { get; private set; } = string.Empty;
    public UserId? OwnerUserId { get; private set; }
    public bool IsSystemPack { get; private set; }
    public int SortOrder { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    private StickerPack()
    {
    }

    public static StickerPack CreateSystem(
        string slug,
        string name,
        string authorName,
        int sortOrder,
        DateTimeOffset nowUtc,
        StickerPackId? id = null)
    {
        if (!StickerConstraints.IsValidSlug(slug))
        {
            throw new DomainException("System sticker pack slug must match lowercase letters, digits, and hyphens.");
        }

        var normalizedName = NormalizeRequiredText(name, StickerConstraints.MaxPackNameLength, "Pack name");
        var normalizedAuthor = NormalizeRequiredText(authorName, StickerConstraints.MaxAuthorNameLength, "Author name");

        return new StickerPack
        {
            Id = id!,
            Slug = slug,
            Name = normalizedName,
            AuthorName = normalizedAuthor,
            OwnerUserId = null,
            IsSystemPack = true,
            SortOrder = sortOrder,
            IsDeleted = false,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            RowVersion = []
        };
    }

    public static StickerPack CreateCustom(
        UserId ownerUserId,
        string name,
        string authorName,
        int sortOrder,
        DateTimeOffset nowUtc,
        StickerPackId? id = null)
    {
        ArgumentNullException.ThrowIfNull(ownerUserId);

        var normalizedName = NormalizeRequiredText(name, StickerConstraints.MaxPackNameLength, "Pack name");
        var normalizedAuthor = NormalizeRequiredText(authorName, StickerConstraints.MaxAuthorNameLength, "Author name");

        return new StickerPack
        {
            Id = id!,
            Slug = null,
            Name = normalizedName,
            AuthorName = normalizedAuthor,
            OwnerUserId = ownerUserId,
            IsSystemPack = false,
            SortOrder = sortOrder,
            IsDeleted = false,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            RowVersion = []
        };
    }

    public void UpdateSystemMetadata(string name, string authorName, int sortOrder, DateTimeOffset nowUtc)
    {
        if (!IsSystemPack)
        {
            throw new DomainException("Cannot update system metadata on a custom pack.");
        }

        Name = NormalizeRequiredText(name, StickerConstraints.MaxPackNameLength, "Pack name");
        AuthorName = NormalizeRequiredText(authorName, StickerConstraints.MaxAuthorNameLength, "Author name");
        SortOrder = sortOrder;
        IsDeleted = false;
        UpdatedAtUtc = nowUtc;
    }

    public void Rename(string name, DateTimeOffset nowUtc)
    {
        if (IsSystemPack)
        {
            throw new DomainException("Built-in starter packs cannot be renamed.");
        }

        if (IsDeleted)
        {
            throw new DomainException("Deleted sticker packs cannot be renamed.");
        }

        Name = NormalizeRequiredText(name, StickerConstraints.MaxPackNameLength, "Pack name");
        UpdatedAtUtc = nowUtc;
    }

    public void MarkDeleted(DateTimeOffset nowUtc)
    {
        if (IsSystemPack)
        {
            throw new DomainException("Built-in starter packs cannot be deleted.");
        }

        if (IsDeleted)
        {
            return;
        }

        IsDeleted = true;
        UpdatedAtUtc = nowUtc;
    }

    private static string NormalizeRequiredText(string? value, int maxLength, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{fieldName} cannot be empty.");
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new DomainException($"{fieldName} cannot exceed {maxLength} characters.");
        }

        return trimmed;
    }
}
