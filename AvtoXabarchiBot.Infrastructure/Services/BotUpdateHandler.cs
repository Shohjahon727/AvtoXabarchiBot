using AvtoXabarchiBot.Core.Constants;
using AvtoXabarchiBot.Core.Enums;
using AvtoXabarchiBot.Core.Helpers;
using AvtoXabarchiBot.Core.Interfaces;
using AvtoXabarchiBot.Core.Models;
using BotUser = AvtoXabarchiBot.Core.Models.User;
using AvtoXabarchiBot.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace AvtoXabarchiBot.Infrastructure.Services;

public class BotUpdateHandler
{
	private readonly AppDbContext _db;
	private readonly ConversationStateService _state;
	private readonly TelegramClientSessionStore _clientStore;
	private readonly IConfiguration _config;
	private readonly SubscriptionAccessService _access;
	private readonly TelegramAccountClientPool _clientPool;
	private readonly TelegramAccountSessionManager _sessionManager;
	private readonly ILogger<BotUpdateHandler> _logger;
	private readonly ILogger<TelegramClientService> _clientLogger;

	public BotUpdateHandler(
		AppDbContext db,
		ConversationStateService state,
		TelegramClientSessionStore clientStore,
		TelegramAccountClientPool clientPool,
		TelegramAccountSessionManager sessionManager,
		SubscriptionAccessService access,
		IConfiguration config,
		ILogger<BotUpdateHandler> logger,
		ILogger<TelegramClientService> clientLogger)
	{
		_db = db;
		_state = state;
		_clientStore = clientStore;
		_clientPool = clientPool;
		_sessionManager = sessionManager;
		_access = access;
		_config = config;
		_logger = logger;
		_clientLogger = clientLogger;
	}

	private TelegramClientService CreateClient() => new(_clientLogger);

	public async Task HandleAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
	{
		if (update.CallbackQuery is { } callback)
		{
			await HandleCallbackAsync(bot, callback, ct);
			return;
		}

		if (update.Message is not { } message) return;

		var chatId = message.Chat.Id;
		var userId = message.From?.Id ?? chatId;

		if (message.Text == "/start")
		{
			await SendWelcomeAsync(bot, chatId, ct);
			return;
		}

		if (message.Contact != null)
		{
			await RegisterUserAsync(bot, chatId, userId, message, ct);
			return;
		}

		if (message.Text == BotButtons.Cancel)
		{
			await CancelFlowAsync(bot, chatId, userId, ct);
			return;
		}

		var dbUser = await GetUserByTelegramIdAsync(userId);
		if (dbUser == null && message.Text != "/start")
		{
			await SendWelcomeAsync(bot, chatId, ct);
			return;
		}

		if (message.Photo?.Length > 0)
		{
			await HandlePhotoAsync(bot, chatId, userId, dbUser!, message, ct);
			return;
		}

		if (message.Text is not { } text) return;

		if (await HandleMenuAsync(bot, chatId, userId, dbUser!, text, ct))
			return;

		await HandleStateInputAsync(bot, chatId, userId, dbUser!, text, ct);
	}

	private async Task HandleCallbackAsync(ITelegramBotClient bot, CallbackQuery callback, CancellationToken ct)
	{
		var chatId = callback.Message?.Chat.Id ?? callback.From.Id;
		var userId = callback.From.Id;
		var data = callback.Data ?? "";
		var messageId = callback.Message?.MessageId ?? 0;

		if (data.StartsWith("pay_ok_") && long.TryParse(data["pay_ok_".Length..], out var approveId))
		{
			await HandleAdminApproveAsync(bot, callback, approveId, messageId, ct);
			return;
		}

		if (data.StartsWith("pay_no_") && long.TryParse(data["pay_no_".Length..], out var rejectId))
		{
			await HandleAdminRejectAsync(bot, callback, rejectId, messageId, ct);
			return;
		}

		await bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);

		if (data == CallbackData.Cancel || data == CallbackData.Back)
		{
			await CancelFlowAsync(bot, chatId, userId, ct);
			if (messageId > 0)
				await bot.EditMessageReplyMarkup(chatId, messageId, replyMarkup: null, cancellationToken: ct);
			return;
		}

		var dbUser = await GetUserByTelegramIdAsync(userId);
		if (dbUser == null) return;

		if (IsPaidFeatureCallback(data) && !await EnsureAccessAsync(bot, chatId, dbUser, ct))
			return;

		if (data == CallbackData.MsgList)
		{
			await ShowMessageListAsync(bot, chatId, dbUser, ct);
			return;
		}

		if (data.StartsWith("msg_detail_") && long.TryParse(data["msg_detail_".Length..], out var detailId))
		{
			await ShowMessageDetailAsync(bot, chatId, dbUser, detailId, ct);
			return;
		}

		if (data.StartsWith("msg_pause_") && long.TryParse(data["msg_pause_".Length..], out var pauseId))
		{
			await SetMessageStatusAsync(bot, chatId, dbUser, pauseId, MessageStatus.Paused, "⏸ Xabar to'xtatildi.", ct);
			return;
		}

		if (data.StartsWith("msg_resume_") && long.TryParse(data["msg_resume_".Length..], out var resumeId))
		{
			var msg = await GetUserMessageAsync(dbUser, resumeId);
			if (msg != null)
			{
				msg.Status = MessageStatus.Active;
				msg.NextSendAt = TashkentTime.UtcNow;
				await _db.SaveChangesAsync(ct);
			}
			await SetMessageStatusAsync(bot, chatId, dbUser, resumeId, MessageStatus.Active, "▶️ Xabar davom ettirildi.", ct);
			return;
		}

		if (data.StartsWith("msg_delete_") && long.TryParse(data["msg_delete_".Length..], out var deleteId))
		{
			var msg = await GetUserMessageAsync(dbUser, deleteId);
			if (msg != null)
			{
				msg.Status = MessageStatus.Deleted;
				await _db.SaveChangesAsync(ct);
			}
			await bot.SendMessage(chatId, "🗑 Xabar o'chirildi.", replyMarkup: MainMenu(), cancellationToken: ct);
			return;
		}

		if (data.StartsWith("msg_edit_") && long.TryParse(data["msg_edit_".Length..], out var editId))
		{
			var session = _state.GetOrCreate(userId);
			session.State = UserConversationState.WaitingMessageTextEdit;
			session.EditingMessageId = editId;
			var msg = await GetUserMessageAsync(dbUser, editId);
			await bot.SendMessage(chatId,
				$"✏️ <b>Xabar matnini o'zgartirish</b>\n\nHozirgi matn:\n<pre>{EscapeHtml(msg?.Content ?? "")}</pre>\n\nYangi xabar matnini yuboring:",
				parseMode: ParseMode.Html,
				replyMarkup: InlineCancel(),
				cancellationToken: ct);
			return;
		}

		if (data == CallbackData.ChangePlan)
		{
			await ShowPlanListAsync(bot, chatId, ct);
			return;
		}

		if (data == CallbackData.ExtendSub)
		{
			await ShowPlanListAsync(bot, chatId, ct);
			return;
		}

		if (data.StartsWith("plan_") && Enum.TryParse<SubscriptionPlan>(data["plan_".Length..], out var plan))
		{
			await StartPaymentFlowAsync(bot, chatId, userId, dbUser, plan, ct);
			return;
		}

		if (data.StartsWith("grp_t_"))
		{
			var parts = data.Split('_');
			if (parts.Length >= 4 && long.TryParse(parts[2], out var accId) && long.TryParse(parts[3], out var grpId))
				await ToggleGroupAsync(bot, chatId, userId, accId, grpId, messageId, ct);
			return;
		}

		if (data.StartsWith("grp_all_") && long.TryParse(data["grp_all_".Length..], out var allAccId))
		{
			await SelectAllGroupsAsync(bot, chatId, userId, allAccId, messageId, ct);
			return;
		}

		if (data.StartsWith("grp_done_") && long.TryParse(data["grp_done_".Length..], out var doneAccId))
		{
			await ShowIntervalKeyboardAsync(bot, chatId, userId, doneAccId, ct);
			return;
		}

		if (data.StartsWith("int_") && int.TryParse(data["int_".Length..], out var minutes))
		{
			await FinalizeScheduledMessageAsync(bot, chatId, userId, dbUser, minutes, ct);
			return;
		}
	}

	private async Task<bool> HandleMenuAsync(ITelegramBotClient bot, long chatId, long userId, BotUser dbUser, string text, CancellationToken ct)
	{
		switch (text)
		{
			case BotButtons.SendMessage:
				if (!await EnsureAccessAsync(bot, chatId, dbUser, ct)) return true;
				await StartSendMessageFlowAsync(bot, chatId, userId, dbUser, ct);
				return true;
			case BotButtons.MyMessages:
				if (!await EnsureAccessAsync(bot, chatId, dbUser, ct)) return true;
				await ShowMessageListAsync(bot, chatId, dbUser, ct);
				return true;
			case BotButtons.Subscriptions:
				await ShowSubscriptionAsync(bot, chatId, dbUser, ct);
				return true;
			case BotButtons.AddAccount:
				if (!await EnsureAccessAsync(bot, chatId, dbUser, ct)) return true;
				await StartAddAccountFlowAsync(bot, chatId, userId, ct);
				return true;
		}
		return false;
	}

	private async Task HandleStateInputAsync(ITelegramBotClient bot, long chatId, long userId, BotUser dbUser, string text, CancellationToken ct)
	{
		var session = _state.GetOrCreate(userId);

		switch (session.State)
		{
			case UserConversationState.WaitingAccountPhone:
				if (!await EnsureAccessAsync(bot, chatId, dbUser, ct)) return;
				await ProcessAccountPhoneAsync(bot, chatId, userId, dbUser, text, ct);
				break;
			case UserConversationState.WaitingLoginCode:
				if (!await EnsureAccessAsync(bot, chatId, dbUser, ct)) return;
				await ProcessLoginCodeAsync(bot, chatId, userId, dbUser, text, ct);
				break;
			case UserConversationState.Waiting2FAPassword:
				if (!await EnsureAccessAsync(bot, chatId, dbUser, ct)) return;
				await Process2FAAsync(bot, chatId, userId, dbUser, text, ct);
				break;
			case UserConversationState.WaitingMessageContent:
				if (!await EnsureAccessAsync(bot, chatId, dbUser, ct)) return;
				await ProcessMessageContentAsync(bot, chatId, userId, dbUser, text, null, ct);
				break;
			case UserConversationState.WaitingMessageTextEdit:
				if (!await EnsureAccessAsync(bot, chatId, dbUser, ct)) return;
				await ProcessMessageEditAsync(bot, chatId, userId, dbUser, text, ct);
				break;
			case UserConversationState.WaitingReceipt:
				await bot.SendMessage(chatId, "Iltimos, to'lov kvitansiyasini <b>rasm</b> sifatida yuboring.", parseMode: ParseMode.Html, replyMarkup: InlineCancel(), cancellationToken: ct);
				break;
			default:
				break;
		}
	}

	private async Task HandlePhotoAsync(ITelegramBotClient bot, long chatId, long userId, BotUser dbUser, Message message, CancellationToken ct)
	{
		var session = _state.GetOrCreate(userId);

		if (session.State == UserConversationState.WaitingReceipt)
		{
			await ProcessReceiptPhotoAsync(bot, chatId, userId, dbUser, message, ct);
			return;
		}

		if (session.State == UserConversationState.WaitingMessageContent)
		{
			if (!await EnsureAccessAsync(bot, chatId, dbUser, ct)) return;
			if (string.IsNullOrWhiteSpace(message.Caption))
			{
				await bot.SendMessage(chatId,
					"❌ Captionsiz rasm qabul qilinmaydi.\n\nIltimos, rasm bilan birga izoh (caption) yozing.",
					replyMarkup: InlineCancel(),
					cancellationToken: ct);
				return;
			}

			var photo = message.Photo!.OrderByDescending(p => p.FileSize).First();
			var path = await DownloadPhotoAsync(bot, photo.FileId, ct);
			await ProcessMessageContentAsync(bot, chatId, userId, dbUser, message.Caption, path, ct);
		}
	}

	#region Registration

	private async Task SendWelcomeAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
	{
		const string welcomeText = """
			👋 Assalomu alaykum!

			Telegram guruhlarga qo'lda yozib o'tirmang — e'lonlaringizni bot o'zi avtomatik yuboradi! 🚀

			✨ Bot imkoniyatlari:
			• Guruhlarga avtomatik xabar yuborish
			• Matnli va rasmli xabarlarni yuborish
			• Yuborish oralig'larini boshqarish
			• Xabarlar holatini kuzatish

			🔐 Boshlash uchun quyidagi tugma orqali telefon raqamingizni yuboring.

			🎁 24 soat bepul sinov muddati mavjud!
			""";

		var keyboard = new ReplyKeyboardMarkup([[KeyboardButton.WithRequestContact(BotButtons.ShareContact)]])
		{
			ResizeKeyboard = true
		};

		await bot.SendMessage(chatId, welcomeText, replyMarkup: keyboard, cancellationToken: ct);
	}

	private async Task RegisterUserAsync(ITelegramBotClient bot, long chatId, long telegramUserId, Message message, CancellationToken ct)
	{
		var phone = NormalizePhone(message.Contact!.PhoneNumber);
		var user = await _db.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramUserId, ct);

		if (user == null)
		{
			user = new BotUser
			{
				TelegramId = telegramUserId,
				PhoneNumber = phone,
				FirstName = message.From?.FirstName,
				LastName = message.From?.LastName,
				TrialEndsAt = TashkentTime.UtcNow.AddDays(1),
				IsActive = true
			};
			_db.Users.Add(user);
			await _db.SaveChangesAsync(ct);

			_db.Subscriptions.Add(new Subscription
			{
				UserId = user.Id,
				Plan = SubscriptionPlan.Trial,
				StartDate = TashkentTime.UtcNow,
				EndDate = user.TrialEndsAt!.Value,
				PaymentStatus = PaymentStatus.Paid,
				Amount = 0
			});
			await _db.SaveChangesAsync(ct);
		}
		else
		{
			user.PhoneNumber = phone;
			if (user.TrialEndsAt == null)
				user.TrialEndsAt = TashkentTime.UtcNow.AddDays(1);
			await _db.SaveChangesAsync(ct);
		}

		_state.Reset(telegramUserId);

		await bot.SendMessage(chatId,
			$"""
			🎉 Tabriklaymiz! Siz ro'yxatdan o'tdingiz!

			📱 Telefon: {phone}
			🎁 Bepul sinov: 1 kun
			⌛ Sinov tugashi: {TashkentTime.Format(user.TrialEndsAt!.Value)} (Toshkent)

			Sinov muddati tugagach, 💎 Obunalarim bo'limidan tarif sotib olishingiz kerak bo'ladi.
			""",
			replyMarkup: MainMenu(),
			cancellationToken: ct);
	}

	#endregion

	#region Account linking

	private async Task StartAddAccountFlowAsync(ITelegramBotClient bot, long chatId, long userId, CancellationToken ct)
	{
		var session = _state.GetOrCreate(userId);
		session.State = UserConversationState.WaitingAccountPhone;
		session.PendingPhone = null;

		await bot.SendMessage(chatId,
			"""
			📱 Telegram akkauntingizni ulash

			Iltimos, telefon raqamingizni xalqaro formatda yuboring:

			Misol: +998901234567

			⚠️ Tasdiqlash kodini qabul qilish uchun raqamingizga kirishingiz kerak.
			""",
			replyMarkup: InlineCancel(),
			cancellationToken: ct);
	}

	private async Task ProcessAccountPhoneAsync(ITelegramBotClient bot, long chatId, long userId, BotUser dbUser, string text, CancellationToken ct)
	{
		var phone = NormalizePhone(text);
		if (phone.Length < 9)
		{
			await bot.SendMessage(chatId, "❌ Noto'g'ri telefon raqam. Misol: +998901234567", replyMarkup: InlineCancel(), cancellationToken: ct);
			return;
		}

		var apiId = _config.GetValue<int>("Telegram:ApiId");
		var apiHash = _config["Telegram:ApiHash"] ?? "";
		if (apiId == 0 || string.IsNullOrEmpty(apiHash))
		{
			await bot.SendMessage(chatId, "⚠️ Telegram API sozlanmagan. Administratorga murojaat qiling.", cancellationToken: ct);
			return;
		}

		var session = _state.GetOrCreate(userId);
		session.PendingPhone = phone;
		session.State = UserConversationState.WaitingLoginCode;

		var client = _clientStore.GetOrAddLoginClient(userId, CreateClient);
		var sessionPath = TelegramSessionHelper.BuildSessionPath(dbUser.Id, phone);

		try
		{
			var step = await client.BeginLoginAsync(phone, sessionPath, apiId, apiHash);
			if (step == LoginStep.Failed)
			{
				await bot.SendMessage(chatId, "❌ Kod yuborib bo'lmadi. Qaytadan urinib ko'ring.", replyMarkup: MainMenu(), cancellationToken: ct);
				_state.Reset(userId);
				return;
			}

			await bot.SendMessage(chatId,
				"""
				📲 Kod yuborildi!

				Telegram akkauntingizga tasdiqlash kodi yuborildi.

				⚠️ <b>MUHIM:</b> Telegram xavfsizlik siyosati sababli kodni <b>STANDART BO'LMAGAN</b> formatda yuboring:

				• <code>123.45</code> (nuqta bilan)
				• <code>12 34 5</code> (bo'sh joy bilan)
				• <code>1-2-3-4-5</code> (chiziqcha bilan)
				""",
				parseMode: ParseMode.Html,
				replyMarkup: InlineCancel(),
				cancellationToken: ct);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Login boshlashda xato");
			await bot.SendMessage(chatId, "❌ Xatolik yuz berdi. Keyinroq urinib ko'ring.", replyMarkup: MainMenu(), cancellationToken: ct);
			_state.Reset(userId);
			_clientStore.RemoveLoginClient(userId);
		}
	}

	private async Task ProcessLoginCodeAsync(ITelegramBotClient bot, long chatId, long userId, BotUser dbUser, string text, CancellationToken ct)
	{
		var session = _state.GetOrCreate(userId);
		if (!_clientStore.TryGetLoginClient(userId, out var client) && session.PendingPhone == null)
		{
			await bot.SendMessage(chatId, "Sessiya topilmadi. Qaytadan akkaunt qo'shing.", replyMarkup: MainMenu(), cancellationToken: ct);
			_state.Reset(userId);
			return;
		}

		client ??= _clientStore.GetOrAddLoginClient(userId, CreateClient);

		try
		{
			var step = await client.SubmitCodeAsync(text);
			if (step == LoginStep.NeedsPassword)
			{
				session.State = UserConversationState.Waiting2FAPassword;
				await bot.SendMessage(chatId,
					"""
					🔐 <b>Ikki bosqichli autentifikatsiya</b>

					Bu akkauntda 2FA yoqilgan.
					Iltimos, cloud parolingizni yuboring:
					""",
					parseMode: ParseMode.Html,
					replyMarkup: InlineCancel(),
					cancellationToken: ct);
				return;
			}

			if (step != LoginStep.Completed)
			{
				await bot.SendMessage(chatId, "❌ Kod noto'g'ri. Qaytadan yuboring.", replyMarkup: InlineCancel(), cancellationToken: ct);
				return;
			}

			await CompleteAccountLinkAsync(bot, chatId, userId, dbUser, session.PendingPhone!, client, used2Fa: false, ct);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Kod tasdiqlashda xato");
			await bot.SendMessage(chatId, "❌ Kod noto'g'ri yoki muddati tugagan.", replyMarkup: InlineCancel(), cancellationToken: ct);
		}
	}

	private async Task Process2FAAsync(ITelegramBotClient bot, long chatId, long userId, BotUser dbUser, string text, CancellationToken ct)
	{
		var session = _state.GetOrCreate(userId);
		var client = _clientStore.GetOrAddLoginClient(userId, CreateClient);

		try
		{
			var step = await client.SubmitPasswordAsync(text);
			if (step != LoginStep.Completed)
			{
				await bot.SendMessage(chatId, "❌ Parol noto'g'ri.", replyMarkup: InlineCancel(), cancellationToken: ct);
				return;
			}

			await CompleteAccountLinkAsync(bot, chatId, userId, dbUser, session.PendingPhone!, client, used2Fa: true, ct);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "2FA xato");
			await bot.SendMessage(chatId, "❌ Parol noto'g'ri.", replyMarkup: InlineCancel(), cancellationToken: ct);
		}
	}

	private async Task CompleteAccountLinkAsync(ITelegramBotClient bot, long chatId, long userId, BotUser dbUser, string phone, TelegramClientService client, bool used2Fa, CancellationToken ct)
	{
		var groups = await client.GetGroupsAsync();
		var normalizedPhone = NormalizePhone(phone);

		var account = await _db.TelegramAccounts.FirstOrDefaultAsync(
			a => a.UserId == dbUser.Id && a.PhoneNumber == normalizedPhone, ct);

		if (account == null)
		{
			account = new TelegramAccount
			{
				UserId = dbUser.Id,
				PhoneNumber = normalizedPhone,
				SessionData = TelegramSessionHelper.BuildSessionPath(dbUser.Id, normalizedPhone),
				IsConnected = true,
				Has2FA = used2Fa
			};
			_db.TelegramAccounts.Add(account);
			await _db.SaveChangesAsync(ct);
		}
		else
		{
			account.IsConnected = true;
			account.SessionData = TelegramSessionHelper.BuildSessionPath(dbUser.Id, normalizedPhone);
			account.Has2FA = used2Fa;
		}

		var existingGroups = await _db.AccountGroups.Where(g => g.AccountId == account.Id).ToListAsync(ct);
		_db.AccountGroups.RemoveRange(existingGroups);

		foreach (var g in groups)
		{
			_db.AccountGroups.Add(new AccountGroup
			{
				AccountId = account.Id,
				TelegramGroupId = g.Id,
				AccessHash = g.AccessHash,
				Title = g.Title,
				IsChannel = g.IsChannel
			});
		}

		await _db.SaveChangesAsync(ct);

		_clientStore.TryTakeLoginClient(userId, out _);
		_clientPool.Adopt(account.Id, client);

		_state.Reset(userId);

		await bot.SendMessage(chatId,
			$"✅ Akkaunt muvaffaqiyatli ulandi!\n\n📱 Telefon: +{normalizedPhone}\n👥 Guruhlar: {groups.Count} ta\n\nEndi siz o'z guruhlaringizga xabar yuborishingiz mumkin.",
			replyMarkup: MainMenu(),
			cancellationToken: ct);
	}

	#endregion

	#region Messages

	private async Task StartSendMessageFlowAsync(ITelegramBotClient bot, long chatId, long userId, BotUser dbUser, CancellationToken ct)
	{
		var account = await _db.TelegramAccounts.FirstOrDefaultAsync(a => a.UserId == dbUser.Id && a.IsConnected, ct);
		if (account == null)
		{
			await bot.SendMessage(chatId,
				"""
				❌ Akkaunt topilmadi

				Xabar yuborish uchun avval Telegram akkaunt qo'shishingiz kerak.
				➕ Akkaunt qo'shish tugmasini bosing.
				""",
				replyMarkup: MainMenu(),
				cancellationToken: ct);
			return;
		}

		var check = await _clientPool.AcquireAsync(account, ct);
		if (check.SessionExpired)
		{
			await _sessionManager.InvalidateSessionAsync(_db, account, "Sessiya tekshiruvi", ct);
			await bot.SendMessage(chatId,
				"""
				⚠️ Telegram sessiyasi eskirgan (Telegram boshqa joyda chiqarilgan bo'lishi mumkin).

				➕ Akkaunt qo'shish orqali qayta ulang.
				""",
				replyMarkup: MainMenu(),
				cancellationToken: ct);
			return;
		}

		var session = _state.GetOrCreate(userId);
		session.State = UserConversationState.WaitingMessageContent;
		session.DraftAccountId = account.Id;
		session.DraftText = null;
		session.DraftImagePath = null;
		session.SelectedGroupIds.Clear();

		await bot.SendMessage(chatId,
			"""
			✍️ Xabar yaratish

			Guruhlarga yubormoqchi bo'lgan xabaringizni yuboring.

			Siz yuborishingiz mumkin:
			• 📝 Oddiy matn xabar
			• 📸 Rasm + caption (izoh)

			⚠️ Diqqat:
			• Rasm yuborishingiz zarur bo'lsa, caption (izoh) yozish MAJBURIY
			• Captionsiz rasmlar qabul qilinmaydi
			""",
			replyMarkup: InlineCancel(),
			cancellationToken: ct);
	}

	private async Task ProcessMessageContentAsync(ITelegramBotClient bot, long chatId, long userId, BotUser dbUser, string text, string? imagePath, CancellationToken ct)
	{
		var session = _state.GetOrCreate(userId);
		if (session.State != UserConversationState.WaitingMessageContent || session.DraftAccountId == null)
			return;

		session.DraftText = text;
		session.DraftImagePath = imagePath;

		var typeLabel = imagePath != null ? "📸 Rasm + caption" : "📝 Matn xabar";
		await bot.SendMessage(chatId, $"✉️ Xabar qabul qilindi\n\n{typeLabel}", cancellationToken: ct);

		await ShowGroupSelectionAsync(bot, chatId, userId, session.DraftAccountId.Value, null, ct);
	}

	private async Task ShowGroupSelectionAsync(ITelegramBotClient bot, long chatId, long userId, long accountId, int? editMessageId, CancellationToken ct)
	{
		var session = _state.GetOrCreate(userId);
		var groups = await _db.AccountGroups.Where(g => g.AccountId == accountId).OrderBy(g => g.Title).ToListAsync(ct);

		if (groups.Count == 0)
		{
			await bot.SendMessage(chatId, "❌ Guruhlar topilmadi. Avval akkaunt ulang va guruhlarga qo'shiling.", replyMarkup: MainMenu(), cancellationToken: ct);
			return;
		}

		var selected = session.SelectedGroupIds.Count;
		var text = $"""
			📊 Guruhlarni tanlash

			📍 Jami guruhlar: {groups.Count}
			✅ Tanlangan: {selected}

			Xabar yubormoqchi bo'lgan guruhlaringizni tanlang:
			""";

		var markup = BuildGroupKeyboard(accountId, groups, session);

		if (editMessageId.HasValue)
			await bot.EditMessageText(chatId, editMessageId.Value, text, replyMarkup: markup, cancellationToken: ct);
		else
			await bot.SendMessage(chatId, text, replyMarkup: markup, cancellationToken: ct);
	}

	private async Task ToggleGroupAsync(ITelegramBotClient bot, long chatId, long userId, long accountId, long groupId, int messageId, CancellationToken ct)
	{
		var session = _state.GetOrCreate(userId);
		if (session.SelectedGroupIds.Contains(groupId))
			session.SelectedGroupIds.Remove(groupId);
		else
			session.SelectedGroupIds.Add(groupId);

		var groups = await _db.AccountGroups.Where(g => g.AccountId == accountId).OrderBy(g => g.Title).ToListAsync(ct);
		var selected = session.SelectedGroupIds.Count;
		var text = $"""
			📊 Guruhlarni tanlash

			📍 Jami guruhlar: {groups.Count}
			✅ Tanlangan: {selected}

			Xabar yubormoqchi bo'lgan guruhlaringizni tanlang:
			""";

		await bot.EditMessageText(chatId, messageId, text, replyMarkup: BuildGroupKeyboard(accountId, groups, session), cancellationToken: ct);
	}

	private async Task SelectAllGroupsAsync(ITelegramBotClient bot, long chatId, long userId, long accountId, int messageId, CancellationToken ct)
	{
		var session = _state.GetOrCreate(userId);
		var groups = await _db.AccountGroups.Where(g => g.AccountId == accountId).ToListAsync(ct);
		session.SelectedGroupIds = groups.Select(g => g.TelegramGroupId).ToHashSet();

		var text = $"""
			📊 Guruhlarni tanlash

			📍 Jami guruhlar: {groups.Count}
			✅ Tanlangan: {session.SelectedGroupIds.Count}

			Xabar yubormoqchi bo'lgan guruhlaringizni tanlang:
			""";

		await bot.EditMessageText(chatId, messageId, text, replyMarkup: BuildGroupKeyboard(accountId, groups, session), cancellationToken: ct);
	}

	private static InlineKeyboardMarkup BuildGroupKeyboard(long accountId, List<AccountGroup> groups, UserSession session)
	{
		var rows = new List<InlineKeyboardButton[]>();

		foreach (var g in groups.Take(20))
		{
			var icon = session.SelectedGroupIds.Contains(g.TelegramGroupId) ? "✅" : "❌";
			var title = g.Title.Length > 28 ? g.Title[..25] + "..." : g.Title;
			rows.Add([InlineKeyboardButton.WithCallbackData($"{icon} {title}", CallbackData.GroupToggle(accountId, g.TelegramGroupId))]);
		}

		rows.Add([InlineKeyboardButton.WithCallbackData("☑️ Barchasini tanlash", CallbackData.GroupSelectAll(accountId))]);

		if (session.SelectedGroupIds.Count > 0)
			rows.Add([InlineKeyboardButton.WithCallbackData($"✅ Davom etish ({session.SelectedGroupIds.Count} ta)", CallbackData.GroupContinue(accountId))]);

		rows.Add([InlineKeyboardButton.WithCallbackData("◀️ Orqaga", CallbackData.Cancel)]);

		return new InlineKeyboardMarkup(rows);
	}

	private async Task ShowIntervalKeyboardAsync(ITelegramBotClient bot, long chatId, long userId, long accountId, CancellationToken ct)
	{
		var session = _state.GetOrCreate(userId);
		if (session.SelectedGroupIds.Count == 0)
		{
			await bot.SendMessage(chatId, "❌ Kamida bitta guruh tanlang.", cancellationToken: ct);
			return;
		}

		await bot.SendMessage(chatId,
			$"✅ Guruhlar tanlandi ({session.SelectedGroupIds.Count} ta)\n\n⏱ Endi xabar yuborish oralig'ini tanlang:",
			replyMarkup: IntervalKeyboard(),
			cancellationToken: ct);
	}

	private static InlineKeyboardMarkup IntervalKeyboard()
	{
		int[] minutes = [5, 10, 15, 30, 60, 180, 360, 720, 1440];
		var rows = new List<InlineKeyboardButton[]>();
		for (var i = 0; i < minutes.Length; i += 2)
		{
			var row = new List<InlineKeyboardButton>();
			row.Add(IntervalButton(minutes[i]));
			if (i + 1 < minutes.Length)
				row.Add(IntervalButton(minutes[i + 1]));
			rows.Add(row.ToArray());
		}
		rows.Add([InlineKeyboardButton.WithCallbackData("◀️ Orqaga", CallbackData.Cancel)]);
		return new InlineKeyboardMarkup(rows);
	}

	private static InlineKeyboardButton IntervalButton(int minutes)
	{
		var label = minutes switch
		{
			< 60 => $"{minutes} daqiqa",
			60 => "1 soat",
			180 => "3 soat",
			360 => "6 soat",
			720 => "12 soat",
			1440 => "24 soat",
			_ => $"{minutes / 60} soat"
		};
		return InlineKeyboardButton.WithCallbackData(label, CallbackData.Interval(minutes));
	}

	private async Task FinalizeScheduledMessageAsync(ITelegramBotClient bot, long chatId, long userId, BotUser dbUser, int intervalMinutes, CancellationToken ct)
	{
		var session = _state.GetOrCreate(userId);
		if (session.DraftAccountId == null || string.IsNullOrEmpty(session.DraftText))
		{
			await bot.SendMessage(chatId, "❌ Xabar topilmadi. Qaytadan boshlang.", replyMarkup: MainMenu(), cancellationToken: ct);
			_state.Reset(userId);
			return;
		}

		var account = await _db.TelegramAccounts.FindAsync([session.DraftAccountId.Value], ct);
		if (account == null) return;

		var scheduled = new ScheduledMessage
		{
			UserId = dbUser.Id,
			AccountId = account.Id,
			Content = session.DraftText,
			ImagePath = session.DraftImagePath,
			IntervalMinutes = intervalMinutes,
			Status = MessageStatus.Active,
			NextSendAt = TashkentTime.UtcNow,
			CreatedAt = TashkentTime.UtcNow
		};

		_db.ScheduledMessages.Add(scheduled);
		await _db.SaveChangesAsync(ct);

		foreach (var groupId in session.SelectedGroupIds)
		{
			var grp = await _db.AccountGroups.FirstOrDefaultAsync(g => g.AccountId == account.Id && g.TelegramGroupId == groupId, ct);
			if (grp != null)
			{
				_db.MessageGroups.Add(new MessageGroup
				{
					MessageId = scheduled.Id,
					TelegramGroupId = grp.TelegramGroupId,
					GroupTitle = grp.Title
				});
			}
		}

		await _db.SaveChangesAsync(ct);

		// Darhol birinchi yuborish
		var apiId = _config.GetValue<int>("Telegram:ApiId");
		var apiHash = _config["Telegram:ApiHash"] ?? "";
		var sentCount = 0;
		var targetCount = 0;
		var acquire = await _clientPool.AcquireAsync(account, ct);

		if (acquire.SessionExpired)
		{
			await _sessionManager.InvalidateSessionAsync(_db, account, "Sessiya muddati tugagan", ct);
			await bot.SendMessage(chatId,
				"⚠️ Telegram sessiyasi eskirgan.\n\n➕ Akkaunt qo'shish orqali akkauntni qayta ulang.",
				replyMarkup: MainMenu(),
				cancellationToken: ct);
			_state.Reset(userId);
			return;
		}

		if (acquire.Client != null)
		{
			var targetGroups = await _db.AccountGroups
				.Where(g => g.AccountId == account.Id && session.SelectedGroupIds.Contains(g.TelegramGroupId))
				.ToListAsync(ct);

			targetCount = targetGroups.Count;
			foreach (var g in targetGroups)
			{
				try
				{
					if (await acquire.Client.SendMessageToGroupAsync(g.TelegramGroupId, g.AccessHash, g.IsChannel, scheduled.Content, scheduled.ImagePath))
						sentCount++;
				}
				catch (Exception ex) when (TelegramSessionHelper.IsSessionAuthError(ex))
				{
					await _sessionManager.InvalidateSessionAsync(_db, account, "AUTH_KEY birinchi yuborishda", ct);
					break;
				}
			}
		}
		else
		{
			_logger.LogWarning("Birinchi yuborish: client olinmadi, account {AccountId}", account.Id);
		}

		scheduled.LastSentAt = sentCount > 0 ? TashkentTime.UtcNow : null;
		scheduled.NextSendAt = TashkentTime.UtcNow.AddMinutes(intervalMinutes);
		await _db.SaveChangesAsync(ct);

		var contentType = scheduled.ImagePath != null ? "rasm + matn" : "matn";
		_state.Reset(userId);

		var sendNote = sentCount switch
		{
			0 when targetCount > 0 => "\n\n⚠️ Guruhlarga yuborilmadi. ➕ Akkaunt qo'shish orqali akkauntni qayta ulang (guruhlar yangilanadi).",
			_ when sentCount < targetCount => $"\n\n⚠️ {sentCount}/{targetCount} ta guruhga yuborildi.",
			_ => "\n\n💡 Xabar darhol yuborildi va keyin tanlangan oraliqda takrorlanadi."
		};

		await bot.SendMessage(chatId,
			$"""
			✅ Xabar muvaffaqiyatli rejalashtirildi!

			📄 Xabar: {contentType}
			⏱ Oraliq: {SubscriptionPlans.FormatInterval(intervalMinutes)}
			👥 Tanlangan guruhlar: {session.SelectedGroupIds.Count} ta
			📱 Akkaunt: +{account.PhoneNumber}{sendNote}
			""",
			replyMarkup: MainMenu(),
			cancellationToken: ct);
	}

	private async Task ShowMessageListAsync(ITelegramBotClient bot, long chatId, BotUser dbUser, CancellationToken ct)
	{
		var messages = await _db.ScheduledMessages
			.Where(m => m.UserId == dbUser.Id && m.Status != MessageStatus.Deleted)
			.OrderByDescending(m => m.CreatedAt)
			.Take(15)
			.ToListAsync(ct);

		if (messages.Count == 0)
		{
			await bot.SendMessage(chatId, "📋 Hozircha xabarlar yo'q.", replyMarkup: MainMenu(), cancellationToken: ct);
			return;
		}

		var rows = messages.Select(m =>
			new[] { InlineKeyboardButton.WithCallbackData($"📄 Xabar #{m.Id}", CallbackData.MsgDetail(m.Id)) }
		).ToList();

		rows.Add([InlineKeyboardButton.WithCallbackData("🔙 Orqaga", CallbackData.Back)]);

		await bot.SendMessage(chatId, "📋 Mening xabarlarim\n\nRo'yxatdan tanlang:", replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
	}

	private async Task ShowMessageDetailAsync(ITelegramBotClient bot, long chatId, BotUser dbUser, long messageId, CancellationToken ct)
	{
		var msg = await _db.ScheduledMessages
			.Include(m => m.Account)
			.Include(m => m.TargetGroups)
			.FirstOrDefaultAsync(m => m.Id == messageId && m.UserId == dbUser.Id, ct);

		if (msg == null)
		{
			await bot.SendMessage(chatId, "❌ Xabar topilmadi.", cancellationToken: ct);
			return;
		}

		var type = msg.ImagePath != null ? "rasm + matn" : "matn";
		var preview = msg.Content.Length > 200 ? msg.Content[..200] + "..." : msg.Content;
		var nextSend = TashkentTime.FormatNullable(msg.NextSendAt);
		var lastSent = msg.LastSentAt.HasValue
			? TashkentTime.Format(msg.LastSentAt.Value)
			: "Hali yuborilmagan";

		var text = $"""
			📄 <b>Xabar #{msg.Id}</b>

			📝 Xabar turi: {type}
			📝 Xabar matni: {EscapeHtml(preview)}
			📱 Akkaunt: +{msg.Account.PhoneNumber}
			⏰ Oraliq: {SubscriptionPlans.FormatInterval(msg.IntervalMinutes)}
			⏭ Keyingi yuborish: {nextSend} (Toshkent)
			📤 Oxirgi yuborilgan: {lastSent}
			📊 Holat: {SubscriptionPlans.FormatStatus(msg.Status)}
			📅 Yaratilgan: {TashkentTime.Format(msg.CreatedAt)} (Toshkent)
			👥 Guruhlar: {msg.TargetGroups.Count} ta
			""";

		var pauseResume = msg.Status == MessageStatus.Active
			? InlineKeyboardButton.WithCallbackData("⏸ To'xtatish", CallbackData.MsgPause(msg.Id))
			: InlineKeyboardButton.WithCallbackData("▶️ Davom ettirish", CallbackData.MsgResume(msg.Id));

		var keyboard = new InlineKeyboardMarkup(
		[
			[pauseResume],
			[InlineKeyboardButton.WithCallbackData("🗑 O'chirish", CallbackData.MsgDelete(msg.Id))],
			[InlineKeyboardButton.WithCallbackData("✏️ Matnni o'zgartirish", CallbackData.MsgEdit(msg.Id))],
			[InlineKeyboardButton.WithCallbackData("🔙 Orqaga", CallbackData.MsgList)]
		]);

		await bot.SendMessage(chatId, text, parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: ct);
	}

	private async Task ProcessMessageEditAsync(ITelegramBotClient bot, long chatId, long userId, BotUser dbUser, string text, CancellationToken ct)
	{
		var session = _state.GetOrCreate(userId);
		if (session.EditingMessageId == null) return;

		var msg = await GetUserMessageAsync(dbUser, session.EditingMessageId.Value);
		if (msg == null) return;

		msg.Content = text;
		await _db.SaveChangesAsync(ct);
		_state.Reset(userId);

		await bot.SendMessage(chatId, $"✅ Xabar matni muvaffaqiyatli o'zgartirildi!\n\n{text}", replyMarkup: MainMenu(), cancellationToken: ct);
		await ShowMessageDetailAsync(bot, chatId, dbUser, msg.Id, ct);
	}

	private async Task SetMessageStatusAsync(ITelegramBotClient bot, long chatId, BotUser dbUser, long messageId, MessageStatus status, string notice, CancellationToken ct)
	{
		var msg = await GetUserMessageAsync(dbUser, messageId);
		if (msg == null) return;

		msg.Status = status;
		if (status == MessageStatus.Paused)
			msg.NextSendAt = null;
		await _db.SaveChangesAsync(ct);

		await bot.SendMessage(chatId, notice, cancellationToken: ct);
		await ShowMessageDetailAsync(bot, chatId, dbUser, messageId, ct);
	}

	#endregion

	#region Subscriptions

	private async Task ShowSubscriptionAsync(ITelegramBotClient bot, long chatId, BotUser dbUser, CancellationToken ct)
	{
		var access = await _access.GetAccessInfoAsync(dbUser, ct);
		var now = TashkentTime.UtcNow;

		if (access.Type == AccessType.Trial)
		{
			await bot.SendMessage(chatId,
				$"""
				💎 Obunalarim

				🎁 Tarif: Bepul sinov (1 kun)
				⌛ Sinov tugashi: {TashkentTime.Format(access.ExpiresAt!.Value)} (Toshkent)

				Sinov tugagach obuna sotib olishingiz kerak bo'ladi.
				""",
				replyMarkup: PlanListKeyboard(),
				cancellationToken: ct);
			return;
		}

		if (access.Type == AccessType.PendingPayment)
		{
			var p = access.PendingSubscription!;
			await bot.SendMessage(chatId,
				$"""
				💎 Obunalarim

				⏳ To'lovingiz tekshirilmoqda...
				📦 Tarif: {SubscriptionPlans.GetPlanTitle(p.Plan)}
				💰 Summa: {p.Amount:N0} so'm

				Administrator tasdiqlagach obuna faollashadi.
				""",
				replyMarkup: MainMenu(),
				cancellationToken: ct);
			return;
		}

		if (access.Type == AccessType.Paid && access.ActiveSubscription != null)
		{
			var sub = access.ActiveSubscription;
			var planName = SubscriptionPlans.GetPlanTitle(sub.Plan);
			await bot.SendMessage(chatId,
				$"""
				💎 Obunalarim

				🎫 Tarif: {planName}
				📅 Boshlanish: {TashkentTime.Format(sub.StartDate)} (Toshkent)
				📅 Tugash: {TashkentTime.Format(sub.EndDate)} (Toshkent)
				""",
				replyMarkup: new InlineKeyboardMarkup(
				[
					[InlineKeyboardButton.WithCallbackData("🔄 Obunani uzaytirish", CallbackData.ExtendSub)],
					[InlineKeyboardButton.WithCallbackData("📦 Tarifni o'zgartirish", CallbackData.ChangePlan)],
					[InlineKeyboardButton.WithCallbackData("◀️ Orqaga", CallbackData.Back)]
				]),
				cancellationToken: ct);
			return;
		}

		await bot.SendMessage(chatId,
			"""
			💎 Obunalarim

			❌ Sinov muddati tugagan yoki obuna yo'q.

			Tarif tanlang va to'lov kvitansiyasini yuboring:
			🔹 Start — 1 oy (19 000 so'm)
			🔹 Pro — 6 oy (99 000 so'm)
			🔹 Pro max — 12 oy (179 000 so'm)
			""",
			replyMarkup: PlanListKeyboard(),
			cancellationToken: ct);
	}

	private async Task ShowPlanListAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
	{
		const string text = """
			💎 Tariflar

			🔹 Start — 1 oy — 19 000 so'm
			🔹 Pro — 6 oy — 99 000 so'm
			🔹 Pro max — 12 oy — 179 000 so'm
			""";

		await bot.SendMessage(chatId, text, replyMarkup: PlanListKeyboard(), cancellationToken: ct);
	}

	private static InlineKeyboardMarkup PlanListKeyboard() => new(
	[
		[InlineKeyboardButton.WithCallbackData("🔹 Start — 1 oy", CallbackData.Plan(nameof(SubscriptionPlan.Start1Month)))],
		[InlineKeyboardButton.WithCallbackData("🔹 Pro — 6 oy", CallbackData.Plan(nameof(SubscriptionPlan.Pro6Month)))],
		[InlineKeyboardButton.WithCallbackData("🔹 Pro max — 12 oy", CallbackData.Plan(nameof(SubscriptionPlan.ProMax12Month)))],
		[InlineKeyboardButton.WithCallbackData("◀️ Orqaga", CallbackData.Back)]
	]);

	private async Task StartPaymentFlowAsync(ITelegramBotClient bot, long chatId, long userId, BotUser dbUser, SubscriptionPlan plan, CancellationToken ct)
	{
		var access = await _access.GetAccessInfoAsync(dbUser, ct);
		if (access.Type == AccessType.PendingPayment)
		{
			await bot.SendMessage(chatId,
				"⏳ Sizda allaqachon tekshirilayotgan to'lov mavjud. Administrator tasdiqlashini kuting.",
				replyMarkup: MainMenu(),
				cancellationToken: ct);
			return;
		}

		var session = _state.GetOrCreate(userId);
		session.State = UserConversationState.WaitingReceipt;
		session.PendingPlan = plan;

		var title = SubscriptionPlans.GetPlanTitle(plan);
		var months = SubscriptionPlans.GetPlanMonths(plan);
		var price = SubscriptionPlans.GetPlanPrice(plan);
		var card = _config["Payment:CardNumber"] ?? "9860 0000 0000 0000";
		var owner = _config["Payment:CardOwner"] ?? "ADMIN";

		await bot.SendMessage(chatId,
			$"""
			💎 {title} — {months} oy

			💰 Narx: {price:N0} so'm

			💳 Karta: <code>{card}</code>
			👤 Egasi: {owner}

			Iltimos, ushbu tarif uchun to'lov kvitansiyasini yuboring:
			""",
			parseMode: ParseMode.Html,
			replyMarkup: InlineCancel(),
			cancellationToken: ct);
	}

	private async Task ProcessReceiptPhotoAsync(ITelegramBotClient bot, long chatId, long userId, BotUser dbUser, Message message, CancellationToken ct)
	{
		var session = _state.GetOrCreate(userId);
		if (session.PendingPlan == null) return;

		var access = await _access.GetAccessInfoAsync(dbUser, ct);
		if (access.Type == AccessType.PendingPayment)
		{
			await bot.SendMessage(chatId,
				"⏳ Sizda allaqachon tekshirilayotgan to'lov mavjud.",
				replyMarkup: MainMenu(),
				cancellationToken: ct);
			_state.Reset(userId);
			return;
		}

		var photo = message.Photo!.OrderByDescending(p => p.FileSize).First();
		var path = await DownloadPhotoAsync(bot, photo.FileId, ct);

		var plan = session.PendingPlan.Value;
		var price = SubscriptionPlans.GetPlanPrice(plan);
		var planTitle = SubscriptionPlans.GetPlanTitle(plan);

		var subscription = new Subscription
		{
			UserId = dbUser.Id,
			Plan = plan,
			StartDate = TashkentTime.UtcNow,
			EndDate = TashkentTime.UtcNow,
			PaymentStatus = PaymentStatus.Pending,
			Amount = price,
			PaymentReceiptPath = path
		};
		_db.Subscriptions.Add(subscription);
		await _db.SaveChangesAsync(ct);

		var adminKeyboard = new InlineKeyboardMarkup(
		[
			[
				InlineKeyboardButton.WithCallbackData("✅ Tasdiqlash", CallbackData.PayApprove(subscription.Id)),
				InlineKeyboardButton.WithCallbackData("❌ Rad etish", CallbackData.PayReject(subscription.Id))
			]
		]);

		var adminCaption = $"""
			🧾 Yangi to'lov kvitansiyasi

			👤 Foydalanuvchi: {dbUser.TelegramId}
			📱 Telefon: {dbUser.PhoneNumber}
			📦 Tarif: {planTitle}
			💰 Summa: {price:N0} so'm
			🆔 To'lov ID: #{subscription.Id}
			""";

		await NotifyAdminsAsync(bot, photo.FileId, adminCaption, adminKeyboard, ct);

		_state.Reset(userId);
		await bot.SendMessage(chatId,
			"""
			✅ Kvitansiya yuborildi!

			⏳ Administrator tekshiruvdan so'ng obunangiz faollashtiriladi.
			Tasdiqlangach sizga xabar beramiz.
			""",
			replyMarkup: MainMenu(),
			cancellationToken: ct);
	}

	private async Task HandleAdminApproveAsync(ITelegramBotClient bot, CallbackQuery callback, long subscriptionId, int messageId, CancellationToken ct)
	{
		if (!_access.IsAdmin(callback.From.Id))
		{
			await bot.AnswerCallbackQuery(callback.Id, "Ruxsat yo'q", showAlert: true, cancellationToken: ct);
			return;
		}

		var sub = await _access.ApprovePaymentAsync(subscriptionId, callback.From.Id, ct);
		if (sub == null)
		{
			await bot.AnswerCallbackQuery(callback.Id, "To'lov topilmadi yoki allaqachon ko'rib chiqilgan", showAlert: true, cancellationToken: ct);
			return;
		}

		await bot.AnswerCallbackQuery(callback.Id, "✅ Tasdiqlandi", cancellationToken: ct);

		if (messageId > 0 && callback.Message != null)
		{
			var newCaption = (callback.Message.Caption ?? "") + "\n\n✅ <b>TASDIQLANDI</b>";
			try
			{
				await bot.EditMessageCaption(callback.Message.Chat.Id, messageId, newCaption, parseMode: ParseMode.Html, replyMarkup: null, cancellationToken: ct);
			}
			catch { /* ignore */ }
		}

		var planTitle = SubscriptionPlans.GetPlanTitle(sub.Plan);
		try
		{
			await bot.SendMessage(sub.User.TelegramId,
				$"""
				🎉 Obunangiz tasdiqlandi!

				📦 Tarif: {planTitle}
				📅 Tugash sanasi: {TashkentTime.Format(sub.EndDate)} (Toshkent)

				Endi botning barcha funksiyalaridan foydalanishingiz mumkin!
				""",
				replyMarkup: MainMenu(),
				cancellationToken: ct);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Foydalanuvchiga tasdiq xabari yuborilmadi: {UserId}", sub.User.TelegramId);
		}
	}

	private async Task HandleAdminRejectAsync(ITelegramBotClient bot, CallbackQuery callback, long subscriptionId, int messageId, CancellationToken ct)
	{
		if (!_access.IsAdmin(callback.From.Id))
		{
			await bot.AnswerCallbackQuery(callback.Id, "Ruxsat yo'q", showAlert: true, cancellationToken: ct);
			return;
		}

		var sub = await _access.RejectPaymentAsync(subscriptionId, ct);
		if (sub == null)
		{
			await bot.AnswerCallbackQuery(callback.Id, "To'lov topilmadi yoki allaqachon ko'rib chiqilgan", showAlert: true, cancellationToken: ct);
			return;
		}

		await bot.AnswerCallbackQuery(callback.Id, "❌ Rad etildi", cancellationToken: ct);

		if (messageId > 0 && callback.Message != null)
		{
			var newCaption = (callback.Message.Caption ?? "") + "\n\n❌ <b>RAD ETILDI</b>";
			try
			{
				await bot.EditMessageCaption(callback.Message.Chat.Id, messageId, newCaption, parseMode: ParseMode.Html, replyMarkup: null, cancellationToken: ct);
			}
			catch { /* ignore */ }
		}

		try
		{
			await bot.SendMessage(sub.User.TelegramId,
				"""
				❌ To'lovingiz rad etildi.

				Iltimos, to'g'ri kvitansiya yuboring yoki 💎 Obunalarim bo'limidan qayta urinib ko'ring.
				""",
				replyMarkup: MainMenu(),
				cancellationToken: ct);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Foydalanuvchiga rad xabari yuborilmadi: {UserId}", sub.User.TelegramId);
		}
	}

	private async Task NotifyAdminsAsync(ITelegramBotClient bot, string fileId, string caption, InlineKeyboardMarkup keyboard, CancellationToken ct)
	{
		var adminChatId = _config.GetValue<long>("Payment:AdminChatId");
		if (adminChatId != 0)
		{
			try
			{
				await bot.SendPhoto(adminChatId, InputFile.FromFileId(fileId), caption: caption, replyMarkup: keyboard, cancellationToken: ct);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Admin guruhiga kvitansiya yuborilmadi");
			}
		}

		var adminIds = _config.GetSection("Payment:AdminUserIds").Get<long[]>() ?? [];
		var singleAdmin = _config.GetValue<long>("Payment:AdminUserId");
		if (singleAdmin != 0 && !adminIds.Contains(singleAdmin))
			adminIds = [.. adminIds, singleAdmin];

		foreach (var adminId in adminIds.Distinct())
		{
			try
			{
				await bot.SendPhoto(adminId, InputFile.FromFileId(fileId), caption: caption, replyMarkup: keyboard, cancellationToken: ct);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Adminga kvitansiya yuborilmadi: {AdminId}", adminId);
			}
		}
	}

	#endregion

	#region Helpers

	private async Task CancelFlowAsync(ITelegramBotClient bot, long chatId, long userId, CancellationToken ct)
	{
		_clientStore.RemoveLoginClient(userId);
		_state.Reset(userId);
		await bot.SendMessage(chatId, "❌ Bekor qilindi.", replyMarkup: MainMenu(), cancellationToken: ct);
	}

	private async Task<BotUser?> GetUserByTelegramIdAsync(long telegramId) =>
		await _db.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramId);

	private async Task<ScheduledMessage?> GetUserMessageAsync(BotUser user, long messageId) =>
		await _db.ScheduledMessages.FirstOrDefaultAsync(m => m.Id == messageId && m.UserId == user.Id);

	private async Task<bool> EnsureAccessAsync(ITelegramBotClient bot, long chatId, BotUser user, CancellationToken ct)
	{
		var access = await _access.GetAccessInfoAsync(user, ct);
		if (access.CanUseBot) return true;

		if (access.Type == AccessType.PendingPayment)
		{
			await bot.SendMessage(chatId,
				"""
				⏳ To'lovingiz administrator tomonidan tekshirilmoqda.

				Tasdiqlangach botdan foydalanishingiz mumkin bo'ladi.
				""",
				replyMarkup: MainMenu(),
				cancellationToken: ct);
			return false;
		}

		await bot.SendMessage(chatId,
			"""
			❌ Sinov muddati tugagan yoki obuna yo'q.

			💎 Obunalarim bo'limiga kiring, tarif tanlang va to'lov kvitansiyasini yuboring.
			Administrator tasdiqlagach bot ishlaydi.
			""",
			replyMarkup: MainMenu(),
			cancellationToken: ct);
		return false;
	}

	private static bool IsPaidFeatureCallback(string data) =>
		data.StartsWith("msg_") ||
		data.StartsWith("grp_") ||
		data.StartsWith("int_");

	private async Task<string> DownloadPhotoAsync(ITelegramBotClient bot, string fileId, CancellationToken ct)
	{
		var file = await bot.GetFile(fileId, ct);
		Directory.CreateDirectory("uploads");
		var path = Path.Combine("uploads", $"{Guid.NewGuid()}{Path.GetExtension(file.FilePath ?? ".jpg")}");
		await using var fs = File.Create(path);
		await bot.DownloadFile(file.FilePath!, fs, ct);
		return path;
	}

	private static string NormalizePhone(string phone) =>
		TelegramSessionHelper.NormalizePhone(phone);

	private static string EscapeHtml(string text) =>
		text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

	private static ReplyKeyboardMarkup MainMenu() => new(
	[
		[BotButtons.SendMessage, BotButtons.MyMessages],
		[BotButtons.Subscriptions, BotButtons.AddAccount]
	])
	{ ResizeKeyboard = true };

	private static InlineKeyboardMarkup InlineCancel() =>
		new([[InlineKeyboardButton.WithCallbackData(BotButtons.Cancel, CallbackData.Cancel)]]);

	#endregion
}
