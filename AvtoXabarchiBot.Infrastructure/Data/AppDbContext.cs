using AvtoXabarchiBot.Core.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AvtoXabarchiBot.Infrastructure.Data;

public class AppDbContext : DbContext
{
	public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

	public DbSet<User> Users { get; set; }
	public DbSet<TelegramAccount> TelegramAccounts { get; set; }
	public DbSet<ScheduledMessage> ScheduledMessages { get; set; }
	public DbSet<MessageGroup> MessageGroups { get; set; }
	public DbSet<Subscription> Subscriptions { get; set; }
	public DbSet<AccountGroup> AccountGroups { get; set; }

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);

		modelBuilder.Entity<User>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.HasIndex(e => e.TelegramId).IsUnique();
			entity.HasIndex(e => e.PhoneNumber);
		});

		modelBuilder.Entity<TelegramAccount>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.HasIndex(e => e.PhoneNumber);
			entity.HasOne(e => e.User)
				  .WithMany(u => u.Accounts)
				  .HasForeignKey(e => e.UserId)
				  .OnDelete(DeleteBehavior.Cascade);
		});

		modelBuilder.Entity<ScheduledMessage>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.HasIndex(e => e.Status);
			entity.HasIndex(e => e.NextSendAt);
			// User va Account orqali ikki cascade yo'li bo'lmasin
			entity.HasOne(e => e.User)
				  .WithMany(u => u.Messages)
				  .HasForeignKey(e => e.UserId)
				  .OnDelete(DeleteBehavior.Restrict);
			entity.HasOne(e => e.Account)
				  .WithMany(a => a.Messages)
				  .HasForeignKey(e => e.AccountId)
				  .OnDelete(DeleteBehavior.Cascade);
		});

		modelBuilder.Entity<MessageGroup>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.HasOne(e => e.Message)
				  .WithMany(m => m.TargetGroups)
				  .HasForeignKey(e => e.MessageId)
				  .OnDelete(DeleteBehavior.Cascade);
		});

		modelBuilder.Entity<Subscription>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.HasIndex(e => e.EndDate);
			entity.HasOne(e => e.User)
				  .WithMany(u => u.Subscriptions)
				  .HasForeignKey(e => e.UserId)
				  .OnDelete(DeleteBehavior.Cascade);
		});

		modelBuilder.Entity<AccountGroup>(entity =>
		{
			entity.HasKey(e => e.Id);
			entity.HasIndex(e => new { e.AccountId, e.TelegramGroupId }).IsUnique();
			entity.HasOne(e => e.Account)
				  .WithMany(a => a.Groups)
				  .HasForeignKey(e => e.AccountId)
				  .OnDelete(DeleteBehavior.Cascade);
		});
	}
}
