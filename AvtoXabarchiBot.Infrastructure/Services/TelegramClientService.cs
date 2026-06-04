using AvtoXabarchiBot.Core.Interfaces;
using Microsoft.Extensions.Logging;
using TL;
using WTelegram;

namespace AvtoXabarchiBot.Infrastructure.Services;

public class TelegramClientService : ITelegramClientService, IDisposable
{
	private readonly ILogger<TelegramClientService>? _logger;
	private Client? _client;
	private readonly Dictionary<string, string> _config = new();
	private string? _sessionPath;
	private bool _disposed;

	public bool IsAuthenticated => _client?.User != null && !_disposed;

	/// <summary>So'nggi Auth_SendCode qayerga yuborilgani (masalan, Telegram ilovasi).</summary>
	public string? LastCodeDeliveryHint { get; private set; }

	public TelegramClientService(ILogger<TelegramClientService>? logger = null)
	{
		_logger = logger;
	}

	public async Task<LoginStep> BeginLoginAsync(string phoneNumber, string sessionPath, int apiId, string apiHash)
	{
		TelegramSessionHelper.DeleteSessionFiles(sessionPath);
		PrepareConfig(apiId, apiHash, sessionPath);
		DisposeClient();

		_client = new Client(Config);
		_sessionPath = sessionPath;

		LastCodeDeliveryHint = null;
		Task OnOtherHandler(IObject obj)
		{
			if (obj is Auth_SentCode sent)
				LastCodeDeliveryHint = DescribeSentCodeType(sent);
			return Task.CompletedTask;
		}

		_client.OnOther += OnOtherHandler;
		try
		{
			var next = await _client.Login(phoneNumber);
			return MapLoginStep(next);
		}
		finally
		{
			_client.OnOther -= OnOtherHandler;
		}
	}

	public async Task<LoginStep> SubmitCodeAsync(string code)
	{
		if (_client == null) return LoginStep.Failed;

		var digits = new string(code.Where(char.IsDigit).ToArray());
		if (digits.Length is < 4 or > 8) return LoginStep.Failed;

		var next = await _client.Login(digits);
		return MapLoginStep(next);
	}

	public async Task<LoginStep> SubmitPasswordAsync(string password)
	{
		if (_client == null) return LoginStep.Failed;

		var next = await _client.Login(password);
		return MapLoginStep(next);
	}

	public async Task<bool> LoadSessionAsync(string sessionPath, int apiId, string apiHash, CancellationToken ct = default)
	{
		var (ok, _) = await TryLoadSessionAsync(sessionPath, apiId, apiHash, ct);
		return ok;
	}

	public async Task<(bool Success, bool SessionExpired)> TryLoadSessionAsync(
		string sessionPath, int apiId, string apiHash, CancellationToken ct = default)
	{
		if (_client?.User != null && _sessionPath == sessionPath && !_disposed)
		{
			try
			{
				await _client.Messages_GetAllDialogs();
				return (true, false);
			}
			catch (Exception ex) when (TelegramSessionHelper.IsSessionAuthError(ex))
			{
				return (false, true);
			}
		}

		if (!File.Exists(sessionPath))
			return (false, true);

		PrepareConfig(apiId, apiHash, sessionPath);
		DisposeClient();

		_client = new Client(Config);
		_sessionPath = sessionPath;

		try
		{
			var user = await _client.LoginUserIfNeeded();
			if (user == null)
				return (false, false);

			await _client.Messages_GetAllDialogs();
			_logger?.LogInformation("Session yuklandi: {Path}", sessionPath);
			return (true, false);
		}
		catch (Exception ex) when (TelegramSessionHelper.IsSessionAuthError(ex))
		{
			_logger?.LogWarning(ex, "Sessiya yaroqsiz (AUTH_KEY): {Path}", sessionPath);
			TelegramSessionHelper.DeleteSessionFiles(sessionPath);
			DisposeClient();
			return (false, true);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Session yuklashda xato: {Path}", sessionPath);
			DisposeClient();
			return (false, false);
		}
	}

	public async Task<List<GroupInfo>> GetGroupsAsync()
	{
		if (_client?.User == null) return [];

		try
		{
			var dialogs = await _client.Messages_GetAllDialogs();
			var groups = new List<GroupInfo>();

			foreach (var (chatKey, chat) in dialogs.chats)
			{
				if (!chat.IsActive) continue;

				switch (chat)
				{
					case Channel channel when channel.IsGroup:
						groups.Add(new GroupInfo
						{
							Id = chatKey,
							AccessHash = channel.access_hash,
							Title = channel.Title ?? "Guruh",
							IsChannel = true
						});
						break;
					case Chat basicChat:
						groups.Add(new GroupInfo
						{
							Id = chatKey,
							AccessHash = 0,
							Title = basicChat.Title ?? "Guruh",
							IsChannel = false
						});
						break;
				}
			}

			return groups.OrderBy(g => g.Title).ToList();
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Guruhlar ro'yxatini olishda xato");
			return [];
		}
	}

	public async Task<bool> SendMessageToGroupAsync(long chatKey, long accessHash, bool isChannel, string text, string? imagePath = null)
	{
		if (_client?.User == null) return false;

		try
		{
			var chat = await ResolveChatAsync(chatKey);
			if (chat == null)
			{
				_logger?.LogWarning("Guruh topilmadi. ChatKey={ChatKey}", chatKey);
				return false;
			}

			if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
			{
				await using var stream = File.OpenRead(imagePath);
				var file = await _client.UploadFileAsync(stream, Path.GetFileName(imagePath));
				await _client.SendMessageAsync(chat, text, new InputMediaUploadedPhoto { file = file });
			}
			else
			{
				await _client.SendMessageAsync(chat, text);
			}

			_logger?.LogInformation("Xabar yuborildi: {Title} ({ChatKey})", chat.Title, chatKey);
			return true;
		}
		catch (Exception ex) when (TelegramSessionHelper.IsSessionAuthError(ex))
		{
			_logger?.LogWarning(ex, "Yuborishda sessiya yaroqsiz");
			throw;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Guruhga yuborishda xato. ChatKey={ChatKey}", chatKey);
			return false;
		}
	}

	private async Task<ChatBase?> ResolveChatAsync(long chatKey)
	{
		if (_client == null) return null;

		var dialogs = await _client.Messages_GetAllDialogs();
		if (dialogs.chats.TryGetValue(chatKey, out var chat) && chat.IsActive)
			return chat;

		var chats = await _client.Messages_GetAllChats();
		if (chats.chats.TryGetValue(chatKey, out chat) && chat.IsActive)
			return chat;

		return null;
	}

	public async Task DisconnectAsync()
	{
		DisposeClient();
		await Task.CompletedTask;
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		DisposeClient();
	}

	private void DisposeClient()
	{
		_client?.Dispose();
		_client = null;
	}

	private void PrepareConfig(int apiId, string apiHash, string sessionPath)
	{
		var fullPath = TelegramSessionHelper.ResolveSessionPath(sessionPath);
		var dir = Path.GetDirectoryName(fullPath);
		if (!string.IsNullOrEmpty(dir))
			Directory.CreateDirectory(dir);

		_config["api_id"] = apiId.ToString();
		_config["api_hash"] = apiHash;
		_config["session_pathname"] = fullPath;
		_config.Remove("verification_code");
		_config.Remove("password");
	}

	private static string DescribeSentCodeType(Auth_SentCode sent)
	{
		var name = sent.type?.GetType().Name ?? "";
		if (name.Contains("App", StringComparison.OrdinalIgnoreCase)) return "Telegram ilovasida";
		if (name.Contains("Sms", StringComparison.OrdinalIgnoreCase)) return "SMS";
		if (name.Contains("Call", StringComparison.OrdinalIgnoreCase)) return "telefon qo'ng'irog'i";
		if (name.Contains("Fragment", StringComparison.OrdinalIgnoreCase)) return "Fragment SMS";
		if (name.Contains("Firebase", StringComparison.OrdinalIgnoreCase)) return "mobil bildirishnoma";
		return "Telegram";
	}

	private static LoginStep MapLoginStep(string? step) => step switch
	{
		null => LoginStep.Completed,
		"verification_code" => LoginStep.NeedsCode,
		"password" => LoginStep.NeedsPassword,
		_ => LoginStep.Failed
	};

	private string? Config(string what) =>
		_config.TryGetValue(what, out var value) ? value : null;
}
