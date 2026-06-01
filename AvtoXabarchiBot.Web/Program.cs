using AvtoXabarchiBot.Infrastructure.Data;
using AvtoXabarchiBot.Infrastructure.Services;
using AvtoXabarchiBot.Web.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
	.ReadFrom.Configuration(builder.Configuration)
	.CreateLogger();
builder.Host.UseSerilog();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<AppDbContext>(options =>
	options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHangfire(config => config
	.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
	.UseSimpleAssemblyNameTypeSerializer()
	.UseRecommendedSerializerSettings()
	.UseSqlServerStorage(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHangfireServer();

builder.Services.AddSingleton<ConversationStateService>();
builder.Services.AddSingleton<TelegramClientSessionStore>();
builder.Services.AddSingleton<TelegramAccountClientPool>();
builder.Services.AddScoped<TelegramAccountSessionManager>();
builder.Services.AddScoped<BotUpdateHandler>();
builder.Services.AddScoped<SubscriptionAccessService>();
builder.Services.AddScoped<MessageDispatchService>();
builder.Services.AddHostedService<BotBackgroundService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
	app.UseSwagger();
	app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.UseHangfireDashboard("/hangfire");

using (var scope = app.Services.CreateScope())
{
	var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
	db.Database.Migrate();
}

RecurringJob.AddOrUpdate<MessageDispatchService>(
	"dispatch-messages",
	service => service.DispatchDueMessagesAsync(),
	Cron.Minutely);

app.Run();
