using AvtoXabarchiBot.Infrastructure.Services;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AvtoXabarchiBot.Web.Services;

public class BotBackgroundService : BackgroundService
{
	private readonly IServiceProvider _serviceProvider;
	private readonly IConfiguration _config;
	private readonly ILogger<BotBackgroundService> _logger;
	private TelegramBotClient? _botClient;

	public BotBackgroundService(
		IServiceProvider serviceProvider,
		IConfiguration config,
		ILogger<BotBackgroundService> logger)
	{
		_serviceProvider = serviceProvider;
		_config = config;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		var token = _config["Telegram:BotToken"];
		if (string.IsNullOrWhiteSpace(token))
		{
			_logger.LogError("Telegram:BotToken sozlanmagan");
			return;
		}

		_botClient = new TelegramBotClient(token);

		var receiverOptions = new ReceiverOptions
		{
			AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery]
		};

		_botClient.StartReceiving(
			updateHandler: HandleUpdateAsync,
			errorHandler: HandleErrorAsync,
			receiverOptions: receiverOptions,
			cancellationToken: stoppingToken
		);

		var me = await _botClient.GetMe(stoppingToken);
		_logger.LogInformation("Bot ishga tushdi: @{Username}", me.Username);

		await Task.Delay(Timeout.Infinite, stoppingToken);
	}

	private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
	{
		try
		{
			using var scope = _serviceProvider.CreateScope();
			var handler = scope.ServiceProvider.GetRequiredService<BotUpdateHandler>();
			await handler.HandleAsync(bot, update, ct);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Update qayta ishlashda xato");
		}
	}

	private Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken ct)
	{
		_logger.LogError(exception, "Bot polling xatosi");
		return Task.CompletedTask;
	}
}
