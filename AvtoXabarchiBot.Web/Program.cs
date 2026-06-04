using AvtoXabarchiBot.Infrastructure.Data;
using AvtoXabarchiBot.Infrastructure.Services;
using AvtoXabarchiBot.Web.Services;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
	.MinimumLevel.Information()
	.WriteTo.Console()
	.ReadFrom.Configuration(builder.Configuration)
	.CreateLogger();
builder.Host.UseSerilog();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
	?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

builder.Services.AddDbContext<AppDbContext>(options =>
	options.UseSqlServer(connectionString));

builder.Services.AddHangfire(config => config
	.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
	.UseSimpleAssemblyNameTypeSerializer()
	.UseRecommendedSerializerSettings()
	.UseSqlServerStorage(connectionString, new SqlServerStorageOptions
	{
		PrepareSchemaIfNecessary = true
	}));

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

await using (var sql = new SqlConnection(connectionString))
{
	await sql.OpenAsync();
	SqlServerObjectsInstaller.Install(sql);
}

RecurringJob.AddOrUpdate<MessageDispatchService>(
	"dispatch-messages",
	service => service.DispatchDueMessagesAsync(),
	Cron.Minutely);

app.Run();
