using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AOS.Messenger.Infrastructure.Persistence;

/// <summary>
/// Authoritative EF Core database context for AOS.Messenger.
/// Extends ASP.NET Core IdentityDbContext with Guid keys and provides DbSets for domain persistence.
/// </summary>
public class ApplicationDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<UserConversationState> UserConversationStates => Set<UserConversationState>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<UserBlock> UserBlocks => Set<UserBlock>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<UserMessageDeletion> UserMessageDeletions => Set<UserMessageDeletion>();
    public DbSet<MessageReaction> MessageReactions => Set<MessageReaction>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        builder.Entity<IdentityRole<Guid>>(b =>
        {
            b.Property(r => r.Id).HasDefaultValueSql("NEWSEQUENTIALID()");
        });
    }
}
