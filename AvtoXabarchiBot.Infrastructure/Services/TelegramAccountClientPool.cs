using System.Collections.Concurrent;
using AvtoXabarchiBot.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AvtoXabarchiBot.Infrastructure.Services;

public sealed class TelegramClientAcquireResult
{
	public TelegramClientService? Client { get; init; }
	public bool SessionExpired { get; init; }
}

/// <summary>
/// Har bir akkaunt uchun bitta WTelegram client (parallel kirish bloklangan).
/// </summary>
public class TelegramAccountClientPool : IDisposable
{
	private readonly ConcurrentDictionary<long, TelegramClientService> _clients = new();
	private readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();
	private readonly IConfiguration _config;
	private readonly ILogger<TelegramClientService> _clientLogger;
	private readonly ILogger<TelegramAccountClientPool> _logger;
	private bool _disposed;

	public TelegramAccountClientPool(
		IConfiguration config,
		ILogger<TelegramClientService> clientLogger,
		ILogger<TelegramAccountClientPool> logger)
	{
		_config = config;
		_clientLogger = clientLogger;
		_logger = logger;
	}

	public void Adopt(long accountId, TelegramClientService client)
	{
		var sem = GetLock(accountId);
		sem.Wait();
		try
		{
			if (_clients.TryRemove(accountId, out var old))
				old.Dispose();

			_clients[accountId] = client;
			_logger.LogInformation("Akkaunt client poolga qo'shildi: {AccountId}", accountId);
		}
		finally
		{
			sem.Release();
		}
	}

	public async Task<TelegramClientAcquireResult> AcquireAsync(TelegramAccount account, CancellationToken ct = default)
	{
		if (!account.IsConnected)
			return new TelegramClientAcquireResult { SessionExpired = true };

		var sem = GetLock(account.Id);
		await sem.WaitAsync(ct);
		try
		{
			if (_clients.TryGetValue(account.Id, out var existing) && existing.IsAuthenticated)
				return new TelegramClientAcquireResult { Client = existing };

			if (existing != null)
			{
				_clients.TryRemove(account.Id, out _);
				existing.Dispose();
			}

			var apiId = _config.GetValue<int>("Telegram:ApiId");
			var apiHash = _config["Telegram:ApiHash"] ?? "";
			if (apiId == 0 || string.IsNullOrEmpty(apiHash))
			{
				_logger.LogWarning("Telegram ApiId/ApiHash sozlanmagan");
				return new TelegramClientAcquireResult();
			}

			var sessionPath = ResolveSessionPath(account);
			if (!File.Exists(sessionPath))
			{
				_logger.LogWarning("Session fayli yo'q: {Path}", sessionPath);
				return new TelegramClientAcquireResult { SessionExpired = true };
			}

			var client = new TelegramClientService(_clientLogger);
			var (ok, expired) = await client.TryLoadSessionAsync(sessionPath, apiId, apiHash, ct);

			if (expired)
			{
				client.Dispose();
				_logger.LogWarning("AUTH_KEY_UNREGISTERED — sessiya yaroqsiz: account {AccountId}", account.Id);
				return new TelegramClientAcquireResult { SessionExpired = true };
			}

			if (!ok)
			{
				client.Dispose();
				_logger.LogWarning("Session yuklanmadi: account {AccountId}", account.Id);
				return new TelegramClientAcquireResult();
			}

			_clients[account.Id] = client;
			return new TelegramClientAcquireResult { Client = client };
		}
		finally
		{
			sem.Release();
		}
	}

	public void Invalidate(long accountId)
	{
		var sem = GetLock(accountId);
		sem.Wait();
		try
		{
			if (_clients.TryRemove(accountId, out var client))
			{
				client.Dispose();
				_logger.LogInformation("Akkaunt client pooldan olib tashlandi: {AccountId}", accountId);
			}
		}
		finally
		{
			sem.Release();
		}
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;

		foreach (var (accountId, client) in _clients)
		{
			try { client.Dispose(); }
			catch (Exception ex) { _logger.LogWarning(ex, "Client dispose: {AccountId}", accountId); }
		}

		_clients.Clear();
		foreach (var sem in _locks.Values)
			sem.Dispose();
		_locks.Clear();
	}

	private SemaphoreSlim GetLock(long accountId) =>
		_locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));

	private static string ResolveSessionPath(TelegramAccount account)
	{
		var path = !string.IsNullOrWhiteSpace(account.SessionData)
			? account.SessionData
			: TelegramSessionHelper.BuildSessionPath(account.UserId, account.PhoneNumber);
		return TelegramSessionHelper.ResolveSessionPath(path);
	}
}
