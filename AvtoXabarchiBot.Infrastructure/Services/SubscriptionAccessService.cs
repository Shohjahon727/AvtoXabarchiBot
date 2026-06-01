using AvtoXabarchiBot.Core.Constants;
using AvtoXabarchiBot.Core.Helpers;
using AvtoXabarchiBot.Core.Models;
using AvtoXabarchiBot.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AvtoXabarchiBot.Infrastructure.Services;

public enum AccessType
{
	None,
	Trial,
	Paid,
	PendingPayment
}

public record AccessInfo(
	AccessType Type,
	bool CanUseBot,
	DateTime? ExpiresAt,
	Subscription? ActiveSubscription,
	Subscription? PendingSubscription);

public class SubscriptionAccessService
{
	private readonly AppDbContext _db;
	private readonly IConfiguration _config;

	public SubscriptionAccessService(AppDbContext db, IConfiguration config)
	{
		_db = db;
		_config = config;
	}

	public async Task<AccessInfo> GetAccessInfoAsync(User user, CancellationToken ct = default)
	{
		var now = TashkentTime.UtcNow;

		if (user.TrialEndsAt > now)
		{
			return new AccessInfo(
				AccessType.Trial,
				CanUseBot: true,
				ExpiresAt: user.TrialEndsAt,
				ActiveSubscription: null,
				PendingSubscription: null);
		}

		var pending = await _db.Subscriptions
			.Where(s => s.UserId == user.Id && s.PaymentStatus == PaymentStatus.Pending)
			.OrderByDescending(s => s.CreatedAt)
			.FirstOrDefaultAsync(ct);

		if (pending != null)
		{
			return new AccessInfo(
				AccessType.PendingPayment,
				CanUseBot: false,
				ExpiresAt: null,
				ActiveSubscription: null,
				PendingSubscription: pending);
		}

		var paid = await _db.Subscriptions
			.Where(s =>
				s.UserId == user.Id &&
				s.PaymentStatus == PaymentStatus.Paid &&
				s.Plan != SubscriptionPlan.Trial &&
				s.EndDate > now)
			.OrderByDescending(s => s.EndDate)
			.FirstOrDefaultAsync(ct);

		if (paid != null)
		{
			return new AccessInfo(
				AccessType.Paid,
				CanUseBot: true,
				ExpiresAt: paid.EndDate,
				ActiveSubscription: paid,
				PendingSubscription: null);
		}

		return new AccessInfo(AccessType.None, false, null, null, null);
	}

	public async Task<bool> HasActiveAccessAsync(User user, CancellationToken ct = default)
	{
		var info = await GetAccessInfoAsync(user, ct);
		return info.CanUseBot;
	}

	public bool IsAdmin(long telegramUserId)
	{
		var ids = _config.GetSection("Payment:AdminUserIds").Get<long[]>() ?? [];
		if (ids.Contains(telegramUserId)) return true;

		var single = _config.GetValue<long>("Payment:AdminUserId");
		return single != 0 && single == telegramUserId;
	}

	public async Task<Subscription?> ApprovePaymentAsync(long subscriptionId, long adminTelegramId, CancellationToken ct = default)
	{
		var sub = await _db.Subscriptions
			.Include(s => s.User)
			.FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);

		if (sub == null || sub.PaymentStatus != PaymentStatus.Pending)
			return null;

		var now = TashkentTime.UtcNow;
		var months = SubscriptionPlans.GetPlanMonths(sub.Plan);

		var currentEnd = await _db.Subscriptions
			.Where(s =>
				s.UserId == sub.UserId &&
				s.Id != sub.Id &&
				s.PaymentStatus == PaymentStatus.Paid &&
				s.Plan != SubscriptionPlan.Trial &&
				s.EndDate > now)
			.MaxAsync(s => (DateTime?)s.EndDate, ct);

		var startFrom = currentEnd.HasValue && currentEnd > now ? currentEnd.Value : now;

		sub.PaymentStatus = PaymentStatus.Paid;
		sub.StartDate = now;
		sub.EndDate = startFrom.AddMonths(months);

		await _db.SaveChangesAsync(ct);
		return sub;
	}

	public async Task<Subscription?> RejectPaymentAsync(long subscriptionId, CancellationToken ct = default)
	{
		var sub = await _db.Subscriptions
			.Include(s => s.User)
			.FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);

		if (sub == null || sub.PaymentStatus != PaymentStatus.Pending)
			return null;

		sub.PaymentStatus = PaymentStatus.Cancelled;
		await _db.SaveChangesAsync(ct);
		return sub;
	}
}
