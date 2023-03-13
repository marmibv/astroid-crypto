using Microsoft.AspNetCore.Mvc;
using Astroid.Entity;
using Astroid.Web.Models;
using Astroid.Core;
using Microsoft.EntityFrameworkCore;
using Astroid.Providers;
using Astroid.Web.Helpers;
using Microsoft.AspNetCore.Authorization;
using Astroid.Core.Cache;

namespace Astroid.Web;

public class BotsController : SecureController
{
	private readonly IServiceProvider ServiceProvider;

	public BotsController(IServiceProvider serviceProvider, AstroidDb db, ICacheService cache, ILogger<BotsController> logger) : base(db, cache)
	{
		ServiceProvider = serviceProvider;
		Logger = logger;
	}

	[HttpPost("list")]
	public async Task<IActionResult> List([FromBody] MPViewDataList<ADBot> model)
	{
		model ??= new MPViewDataList<ADBot>();

		model = await Db.Bots
			.Where(x => x.UserId == CurrentUser.Id)
			.AsNoTracking()
			.OrderByDescending(x => x.CreatedDate)
			.ViewDataListAsync<ADBot>(model);

		return Success(model.ForJson(x => new AMBot
		{
			Id = x.Id,
			Label = x.Label,
			Description = x.Description,
		}));
	}

	[HttpGet("{id}")]
	public async Task<IActionResult> Get(Guid id)
	{
		if (id == Guid.Empty)
			return BadRequest("Invalid exchange id");

		var bot = await Db.Bots
			.Where(x => x.UserId == CurrentUser.Id)
			.AsNoTracking()
			.FirstOrDefaultAsync(x => x.Id == id);
		if (bot == null)
			return NotFound("Exchange not found");

		return Success(new AMBot
		{
			Id = bot.Id,
			Label = bot.Label,
			Description = bot.Description,
			ExchangeId = bot.ExchangeId,
			OrderType = bot.OrderType,
			OrderMode = bot.OrderMode,
			PositionSizeType = bot.PositionSizeType,
			PositionSize = bot.PositionSize,
			IsPositionSizeExpandable = bot.IsPositionSizeExpandable,
			IsTakePofitEnabled = bot.IsTakePofitEnabled,
			TakeProfitTargets = bot.TakeProfitTargets,
			IsStopLossEnabled = bot.IsStopLossEnabled,
			StopLossPrice = bot.StopLossPrice,
			StopLossCallbackRate = bot.StopLossCallbackRate,
			StopLossActivation = bot.StopLossActivation,
			Key = bot.Key,
			IsEnabled = bot.IsEnabled,
			LimitSettings = bot.LimitSettings
		});
	}

	[HttpPost("save")]
	public async Task<IActionResult> Save([FromBody] AMBot model)
	{
		var isNameExists = await Db.Bots.AnyAsync(x => x.Label == model.Label && x.Id != model.Id);
		if (isNameExists) BadRequest("Exchange name already exists");

		if (model.Id == Guid.Empty)
		{
			var bot = new ADBot
			{
				Id = Guid.NewGuid(),
				Label = model.Label,
				Description = model.Description,
				ExchangeId = model.ExchangeId,
				OrderType = model.OrderType,
				OrderMode = model.OrderMode,
				PositionSizeType = model.PositionSizeType,
				PositionSize = model.PositionSize,
				IsPositionSizeExpandable = model.IsPositionSizeExpandable,
				IsTakePofitEnabled = model.IsTakePofitEnabled,
				TakeProfitTargets = model.TakeProfitTargets,
				IsStopLossEnabled = model.IsStopLossEnabled,
				StopLossPrice = model.StopLossPrice,
				StopLossCallbackRate = model.StopLossCallbackRate,
				StopLossActivation = model.StopLossActivation,
				Key = model.Key,
				CreatedDate = DateTime.Now,
				UserId = CurrentUser.Id,
				IsEnabled = model.IsEnabled,
				LimitSettings = model.LimitSettings
			};

			await Db.Bots.AddAsync(bot);
		}
		else
		{
			var bot = await Db.Bots.FirstOrDefaultAsync(x => x.Id == model.Id);
			if (bot == null)
				return NotFound("Exchange not found");

			bot.Label = model.Label;
			bot.Description = model.Description;
			bot.ExchangeId = model.ExchangeId;
			bot.OrderType = model.OrderType;
			bot.OrderMode = model.OrderMode;
			bot.PositionSizeType = model.PositionSizeType;
			bot.PositionSize = model.PositionSize;
			bot.IsPositionSizeExpandable = model.IsPositionSizeExpandable;
			bot.IsTakePofitEnabled = model.IsTakePofitEnabled;
			bot.TakeProfitTargets = model.TakeProfitTargets;
			bot.IsStopLossEnabled = model.IsStopLossEnabled;
			bot.StopLossPrice = model.StopLossPrice;
			bot.StopLossCallbackRate = model.StopLossCallbackRate;
			bot.StopLossActivation = model.StopLossActivation;
			bot.Key = model.Key;
			bot.IsEnabled = model.IsEnabled;
			bot.ModifiedDate = DateTime.Now;
			bot.LimitSettings = model.LimitSettings;
		}

		await Db.SaveChangesAsync();

		return Success("Exchange saved successfully");
	}

	[AllowAnonymous]
	[HttpPost("execute")]
	public async Task<IActionResult> Execute([FromBody] AMOrderRequest orderRequest)
	{
		var bot = await Db.Bots.AsNoTracking().FirstOrDefaultAsync(x => x.Key == orderRequest.Key);
		if (bot == null) throw new Exception($"Bot {orderRequest.Key} not found");

		if (!bot.IsEnabled) return BadRequest("Bot is disabled");

		if (Cache.IsLocked($"lock:bot:{bot.Id}"))
		{
			await AddAudit(AuditType.OrderRequest, bot.UserId, bot.Id, $"Order request rejected since the bot is already processing an order.");
			return BadRequest("Bot is busy");
		}

		var _ = Cache.AcquireLock($"lock:bot:{bot.Id}");
		var exchange = await Db.Exchanges
			.AsNoTracking()
			.Include(x => x.Provider)
			.FirstOrDefaultAsync(x => x.Id == bot.ExchangeId);
		if (exchange == null) throw new Exception($"Exchange {bot.ExchangeId} not found");

		var exchanger = ExchangerFactory.Create(ServiceProvider, exchange);
		if (exchanger == null) throw new Exception($"Exchanger type {exchange.Provider.Name} not found");

		if (orderRequest == null || string.IsNullOrEmpty(orderRequest.Ticker)) throw new Exception("Ticker is required");

		if (string.IsNullOrEmpty(orderRequest.Type)) throw new Exception("Order type is required");

		try
		{
			var result = await exchanger.ExecuteOrder(bot, orderRequest);
			if (!result.Success) LogError(null, result.Message);

			result.Audits.ForEach(x =>
			{
				x.UserId = exchange.UserId;
				x.ActorId = bot.Id;
				Db.Audits.Add(x);
			});

			await Db.SaveChangesAsync();
		}
		catch (Exception ex)
		{
			LogError(ex, ex.Message);
			return Error(ex.Message);
		}
		finally
		{
			Cache.ReleaseLock($"lock:bot:{bot.Id}");
		}

		return Success(null, "Order executed successfully");
	}

	[HttpDelete("{id}")]
	public async Task<IActionResult> Delete(Guid id)
	{
		if (id == Guid.Empty)
			return BadRequest("Invalid exchange id");

		var bot = await Db.Bots.Where(x => x.UserId == CurrentUser.Id).FirstOrDefaultAsync(x => x.Id == id);
		if (bot == null)
			return NotFound("Exchange not found");

		Db.Bots.Remove(bot);
		await Db.SaveChangesAsync();

		return Success("Exchange deleted successfully");
	}

	[NonAction]
	public async Task AddAudit(AuditType type, Guid userId, Guid botId, string description)
	{
		var audit = new ADAudit
		{
			Id = Guid.NewGuid(),
			UserId = userId,
			ActorId = botId,
			Type = type,
			Description = description,
			CorrelationId = ExchangeProviderBase.GenerateCorrelationId(),
			CreatedDate = DateTime.UtcNow,
		};

		await Db.Audits.AddAsync(audit);
		await Db.SaveChangesAsync();
	}
}