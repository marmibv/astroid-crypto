
using Astroid.Core.Cache;
using Astroid.Core;
using Astroid.Entity;
using Astroid.Providers;
using Microsoft.EntityFrameworkCore;
using Astroid.Entity.Extentions;
using Astroid.Core.MessageQueue;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Astroid.BackgroundServices.Cache;

public class OrderExecutor : IHostedService
{
	private IServiceProvider ServiceProvider { get; set; }
	private ExchangeInfoStore ExchangeStore { get; set; }
	private ICacheService Cache { get; set; }
	private ILogger<OrderExecutor> Logger { get; set; }
	private AstroidDb Db { get; set; }
	private AQOrder OrderQueue { get; set; }
	private List<IDisposable> Consumers { get; set; } = new();

	private const string SubscriptionId1 = "1";
	private const string SubscriptionId2 = "2";
	private const string SubscriptionId3 = "3";

	public OrderExecutor(IServiceProvider serviceProvider, AstroidDb db, ExchangeInfoStore exchangeStore, AQOrder orderQueue, ICacheService cache, ILogger<OrderExecutor> logger)
	{
		ServiceProvider = serviceProvider;
		Db = db;
		ExchangeStore = exchangeStore;
		OrderQueue = orderQueue;
		Cache = cache;
		Logger = logger;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		Logger.LogInformation("Starting Order Executor Service.");
		_ = Task.Run(() => DoJob(cancellationToken), cancellationToken);

		return Task.CompletedTask;
	}

	public async Task DoJob(CancellationToken cancellationToken)
	{
		var consumer1 = await OrderQueue.Subscribe(SubscriptionId1, (msg, ct) => ExecuteOrder(ServiceProvider, msg, ct), cancellationToken);
		var consumer2 = await OrderQueue.Subscribe(SubscriptionId2, (msg, ct) => ExecuteOrder(ServiceProvider, msg, ct), cancellationToken);
		var consumer3 = await OrderQueue.Subscribe(SubscriptionId3, (msg, ct) => ExecuteOrder(ServiceProvider, msg, ct), cancellationToken);

		Consumers.Add(consumer1);
		Consumers.Add(consumer2);
		Consumers.Add(consumer3);

		while (!cancellationToken.IsCancellationRequested)
		{
			await Task.Delay(1000 * 60 * 10, cancellationToken);
		}
	}

	public async Task ExecuteOrder(IServiceProvider sp, AQOrderMessage message, CancellationToken cancellationToken = default)
	{
		var scope = sp.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<AstroidDb>();
		Logger.LogInformation($"Executing order {message.OrderId}.");

		var order = await GetOrder(db, message.OrderId, cancellationToken);
		if (order == null)
		{
			Logger.LogError($"Order not found for {message.OrderId}.");
			return;
		}

		var exchanger = ExchangerFactory.Create(ServiceProvider, order.Exchange);
		if (exchanger == null)
		{
			await AddAudit(db, order, AuditType.OpenOrderPlaced, $"Exchanger type {order.Exchange.Label} not found", order.Position.Id.ToString());
			return;
		}

		var request = GenerateRequest(order);
		var result = await exchanger.ExecuteOrder(order.Bot, request);
		result.Audits.ForEach(x =>
		{
			x.UserId = order.UserId;
			x.ActorId = order.BotId;
			x.TargetId = order.Id;
			x.CorrelationId = result.CorrelationId;
			db.Audits.Add(x);
		});

		if (!result.Success)
			Logger.LogError($"Order execution failed for {order.Id}.");

		await db.SaveChangesAsync(cancellationToken);
	}

	public AMOrderRequest GenerateRequest(ADOrder order)
	{
		var request = new AMOrderRequest
		{
			OrderId = order.Id,
			Ticker = order.Symbol,
			Leverage = order.Position.Leverage,
			Type = GetType(order),
			Quantity = order.Quantity,
			QuantityType = QuantityType.Exact,
			Key = order.Bot.Key
		};

		return request;
	}

	public string GetType(ADOrder order)
	{
		switch (order.TriggerType)
		{
			case OrderTriggerType.StopLoss:
				return order.Position.Type == PositionType.Long ? "close-long" : "close-short";
			case OrderTriggerType.TakeProfit:
				return order.Position.Type == PositionType.Long ? "close-long" : "close-short";
			case OrderTriggerType.Pyramiding:
				return order.Position.Type == PositionType.Long ? "open-long" : "open-short";
			default:
				throw new InvalidDataException("Invalid order trigger type.");
		}
	}

	public async Task<ADOrder?> GetOrder(AstroidDb db, Guid orderId, CancellationToken cancellationToken = default) =>
		await db.Orders
			.AsNoTracking()
			.Include(x => x.Position)
			.Include(x => x.Bot)
			.Include(x => x.Exchange)
				.ThenInclude(x => x.Provider)
			.FirstOrDefaultAsync(x => x.Id == orderId, cancellationToken);

	public async Task AddAudit(AstroidDb db, ADOrder order, AuditType type, string description, string? corellationId = null) => await db.AddAudit(order.UserId, order.BotId, type, description, order.Id, corellationId);

	public Task StopAsync(CancellationToken cancellationToken)
	{
		Consumers.ForEach(x => x.Dispose());
		return Task.CompletedTask;
	}
}
