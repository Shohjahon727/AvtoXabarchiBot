using AvtoXabarchiBot.Core.Helpers;
using AvtoXabarchiBot.Core.Models;
using AvtoXabarchiBot.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AvtoXabarchiBot.Infrastructure.Services;

public class MessageDispatchService
{
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly ILogger<MessageDispatchService> _logger;

	public MessageDispatchService(IServiceScopeFactory scopeFactory, ILogger<MessageDispatchService> logger)
	{
		_scopeFactory = scopeFactory;
		_logger = logger;
	}

	public async Task DispatchDueMessagesAsync()
	{
		using var scope = _scopeFactory.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
		var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
		var clientPool = scope.ServiceProvider.GetRequiredService<TelegramAccountClientPool>();
		var sessionManager = scope.ServiceProvider.GetRequiredService<TelegramAccountSessionManager>();
		var accessService = scope.ServiceProvider.GetRequiredService<SubscriptionAccessService>();

		var apiId = config.GetValue<int>("Telegram:ApiId");
		var apiHash = config["Telegram:ApiHash"] ?? "";

		if (apiId == 0 || string.IsNullOrEmpty(apiHash)) return;

		var now = TashkentTime.UtcNow;
		var messages = await db.ScheduledMessages
			.Include(m => m.TargetGroups)
			.Include(m => m.Account)
			.Include(m => m.User)
			.Where(m =>
				m.Status == MessageStatus.Active &&
				m.NextSendAt != null &&
				m.NextSendAt <= now &&
				m.Account.IsConnected)
			.ToListAsync();

		foreach (var message in messages)
		{
			if (!await accessService.HasActiveAccessAsync(message.User))
			{
				message.Status = MessageStatus.Paused;
				continue;
			}

			await SendOneAsync(db, clientPool, sessionManager, message);
		}

		if (messages.Count > 0)
			await db.SaveChangesAsync();
	}

	private async Task SendOneAsync(
		AppDbContext db,
		TelegramAccountClientPool clientPool,
		TelegramAccountSessionManager sessionManager,
		ScheduledMessage message)
	{
		var acquire = await clientPool.AcquireAsync(message.Account);
		if (acquire.SessionExpired)
		{
			await sessionManager.InvalidateSessionAsync(db, message.Account, "AUTH_KEY_UNREGISTERED (rejalashtirilgan yuborish)");
			message.Status = MessageStatus.Paused;
			return;
		}

		if (acquire.Client == null)
			return;

		var client = acquire.Client;
		var accountGroups = await db.AccountGroups
			.Where(g => g.AccountId == message.AccountId)
			.ToListAsync();

		var targetIds = message.TargetGroups.Select(g => g.TelegramGroupId).ToHashSet();
		var groups = accountGroups.Where(g => targetIds.Contains(g.TelegramGroupId)).ToList();

		var sent = 0;
		var sessionExpired = false;

		foreach (var group in groups)
		{
			try
			{
				var ok = await client.SendMessageToGroupAsync(
					group.TelegramGroupId,
					group.AccessHash,
					group.IsChannel,
					message.Content,
					message.ImagePath);

				if (ok) sent++;
			}
			catch (Exception ex) when (TelegramSessionHelper.IsSessionAuthError(ex))
			{
				sessionExpired = true;
				break;
			}
		}

		if (sessionExpired)
		{
			await sessionManager.InvalidateSessionAsync(db, message.Account, "AUTH_KEY yuborish paytida");
			message.Status = MessageStatus.Paused;
			return;
		}

		if (sent == 0 && groups.Count > 0)
			_logger.LogWarning("Xabar #{MessageId}: hech qaysi guruhga yuborilmadi ({Count} ta)", message.Id, groups.Count);
		else if (groups.Count > 0)
			_logger.LogInformation("Xabar #{MessageId}: {Sent}/{Total} guruhga yuborildi", message.Id, sent, groups.Count);

		message.LastSentAt = TashkentTime.UtcNow;
		message.NextSendAt = TashkentTime.UtcNow.AddMinutes(message.IntervalMinutes);
		message.Account.LastUsedAt = TashkentTime.UtcNow;
	}
}
