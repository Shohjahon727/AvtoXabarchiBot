using System.Text.RegularExpressions;
using AvtoXabarchiBot.Core.Models;
using AvtoXabarchiBot.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TL;

namespace AvtoXabarchiBot.Infrastructure.Services;

public static class TelegramSessionHelper
{
	public static string BuildSessionPath(long userId, string phone)
	{
		var sessionsDir = Path.Combine(AppContext.BaseDirectory, "sessions");
		Directory.CreateDirectory(sessionsDir);
		return Path.Combine(sessionsDir, $"user_{userId}_{NormalizePhone(phone)}.session");
	}

	public static string ResolveSessionPath(string sessionPath)
	{
		if (string.IsNullOrWhiteSpace(sessionPath))
			return sessionPath;

		return Path.IsPathRooted(sessionPath)
			? Path.GetFullPath(sessionPath)
			: Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, sessionPath));
	}

	public static string NormalizePhone(string phone) =>
		new string(phone.Where(char.IsDigit).ToArray());

	public static bool TryGetFloodWaitSeconds(Exception ex, out int seconds)
	{
		seconds = 0;
		var text = ex is RpcException rpc ? rpc.Message : ex.Message;
		var match = Regex.Match(text, @"FLOOD_WAIT_(\d+)", RegexOptions.IgnoreCase);
		if (!match.Success || !int.TryParse(match.Groups[1].Value, out seconds))
			return false;
		return seconds > 0;
	}

	public static string FormatFloodWaitMessage(int seconds)
	{
		if (seconds < 60)
			return $"⏳ Telegram vaqtincha cheklov qo‘ydi.\n\nIltimos, <b>{seconds} soniya</b> kutib, keyin qayta «Akkaunt qo'shish» ni bosing.";

		var minutes = (int)Math.Ceiling(seconds / 60.0);
		return $"⏳ Telegram juda ko‘p marta kod so‘ralgani uchun vaqtincha blokladi.\n\nIltimos, taxminan <b>{minutes} daqiqa</b> kuting (yangi kod so‘ramang), so‘ng qayta «Akkaunt qo'shish» ni bosing.";
	}

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
