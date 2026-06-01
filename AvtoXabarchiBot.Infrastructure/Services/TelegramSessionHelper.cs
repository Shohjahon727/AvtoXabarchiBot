using AvtoXabarchiBot.Core.Models;
using AvtoXabarchiBot.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TL;

namespace AvtoXabarchiBot.Infrastructure.Services;

public static class TelegramSessionHelper
{
	public static string BuildSessionPath(long userId, string phone) =>
		$"sessions/user_{userId}_{NormalizePhone(phone)}.session";

	public static string NormalizePhone(string phone) =>
		new string(phone.Where(char.IsDigit).ToArray());

	public static bool IsSessionAuthError(Exception ex)
	{
		if (ex is RpcException rpc)
		{
			if (rpc.Code == 401) return true;
			if (rpc.Message.Contains("AUTH_KEY", StringComparison.OrdinalIgnoreCase)) return true;
		}

		var text = ex.ToString();
		return text.Contains("AUTH_KEY_UNREGISTERED", StringComparison.OrdinalIgnoreCase)
		       || text.Contains("SESSION_REVOKED", StringComparison.OrdinalIgnoreCase)
		       || text.Contains("USER_DEACTIVATED", StringComparison.OrdinalIgnoreCase);
	}

	public static void DeleteSessionFiles(string sessionPath)
	{
		try
		{
			if (File.Exists(sessionPath))
				File.Delete(sessionPath);

			var dir = Path.GetDirectoryName(Path.GetFullPath(sessionPath));
			var name = Path.GetFileNameWithoutExtension(sessionPath);
			if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;

			foreach (var file in Directory.GetFiles(dir, $"{name}*"))
			{
				try { File.Delete(file); } catch { /* ignore */ }
			}
		}
		catch
		{
			// ignore
		}
	}
}

public class TelegramAccountSessionManager
{
	private readonly TelegramAccountClientPool _pool;
	private readonly ILogger<TelegramAccountSessionManager> _logger;

	public TelegramAccountSessionManager(
		TelegramAccountClientPool pool,
		ILogger<TelegramAccountSessionManager> logger)
	{
		_pool = pool;
		_logger = logger;
	}

	public async Task InvalidateSessionAsync(AppDbContext db, TelegramAccount account, string reason, CancellationToken ct = default)
	{
		_pool.Invalidate(account.Id);

		if (!string.IsNullOrWhiteSpace(account.SessionData))
			TelegramSessionHelper.DeleteSessionFiles(account.SessionData);

		account.IsConnected = false;
		await db.SaveChangesAsync(ct);

		_logger.LogWarning(
			"Akkaunt sessiyasi bekor qilindi (qayta ulash kerak). AccountId={AccountId}, Phone={Phone}, Sabab={Reason}",
			account.Id, account.PhoneNumber, reason);
	}
}
