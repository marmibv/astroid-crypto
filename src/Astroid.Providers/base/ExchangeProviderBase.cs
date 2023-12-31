using System.ComponentModel;
using System.Reflection;
using Astroid.Core;
using Astroid.Entity;
using Astroid.Entity.Extentions;
using CryptoExchange.Net.CommonObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

namespace Astroid.Providers;

public abstract class ExchangeProviderBase : IDisposable
{
	protected AstroidDb Db { get; set; }
	protected ExchangeInfoStore ExchangeStore { get; set; }
	protected ADExchange Exchange { get; set; }
	public string CorrelationId { get; set; }

	protected ExchangeProviderBase() { }

	protected ExchangeProviderBase(AstroidDb db, ExchangeInfoStore infoStore)
	{
		Db = db;
		ExchangeStore = infoStore;
		CorrelationId = GenerateCorrelationId();
	}

	public async Task<int> GetEntryPointIndex(AMOrderBook orderBook, PositionType pSide, LimitSettings settings)
	{
		var entryPoint = await GetEntryPoint(orderBook, pSide, settings);
		var i = pSide == PositionType.Long ? await orderBook.GetGreatestAskPriceLessThan(entryPoint) : await orderBook.GetLeastBidPriceGreaterThan(entryPoint);

		if (i <= 0) throw new Exception("Could not find entry point out of order book.");

		return i;
	}

	public static async Task<decimal> GetEntryPoint(AMOrderBook orderBook, PositionType pSide, LimitSettings settings)
	{
		if (settings.ComputationMethod == OrderBookComputationMethod.Code)
		{
			var pairs = pSide == PositionType.Long ? await orderBook.GetAsks(settings.OrderBookDepth) : await orderBook.GetBids(settings.OrderBookDepth);
			var entries = pairs.Select(x => new AMOrderBookEntry { Price = x.Key, Quantity = x.Value }).ToList();

			var result = CodeExecutor.ExecuteComputationMethod(settings.Code, entries);
			if (!result.IsSuccess) throw new Exception(result.Message);

			return result.Data;
		}

		var prices = pSide == PositionType.Long ? await orderBook.GetAskPrices(settings.OrderBookDepth) : await orderBook.GetBidPrices(settings.OrderBookDepth);

		var sDeviation = ComputeStandardDeviation(prices);
		var mean = prices.Average();

		return pSide == PositionType.Long ? mean + (2 * sDeviation) : mean - (2 * sDeviation);
	}

	public static decimal ComputeStandardDeviation(IEnumerable<decimal> prices)
	{
		var mean = prices.Average();
		var squaredDifferences = prices.Select(p => Math.Pow((double)p - (double)mean, 2));
		var variance = squaredDifferences.Sum() / squaredDifferences.Count();
		var standardDeviation = Math.Sqrt(variance);

		return (decimal)standardDeviation;
	}

	public virtual void Context(ADExchange exchange)
	{
		Exchange = exchange;
		var propertyValues = JsonConvert.DeserializeObject<List<ProviderPropertyValue>>(Exchange.PropertiesJson);
		BindProperties(propertyValues);
	}

	public void BindProperties(List<ProviderPropertyValue> propertyValues)
	{
		if (propertyValues == null) throw new ArgumentException("There is no property to bind this provider.");

		var provider = this;
		var type = GetType();
		var properties = type.GetProperties().Where(x => x.GetCustomAttribute<PropertyMetadataAttribute>() != null).ToList();

		foreach (var property in properties)
		{
			var propertyValue = propertyValues.SingleOrDefault(p => p.Property == property.Name);
			if (propertyValue?.Value == null) continue;

			if (property.PropertyType == propertyValue.Value.GetType())
			{
				property.SetValue(provider, propertyValue.Value);
				continue;
			}

			try
			{
				if (property.PropertyType.IsGenericType)
				{
					var propertyValueString = $"{propertyValue.Value}";
					if (!string.IsNullOrWhiteSpace(propertyValueString))
					{
						property.SetValue(provider,
							JsonConvert.DeserializeObject(propertyValueString, property.PropertyType));
					}

					continue;
				}

				var converter = TypeDescriptor.GetConverter(property.PropertyType);
				var objValue = converter.ConvertFrom(propertyValue.Value.ToString());

				property.SetValue(provider, objValue);
				continue;
			}
			catch
			{
				// Unable to convert basic value
			}

			try
			{
				var value = JsonConvert.DeserializeObject(propertyValue.Value.ToString(), property.PropertyType);
				property.SetValue(provider, value);
			}
			catch
			{
				// Unable to convert complex value
			}
		}
	}

	public static string GenerateCorrelationId()
	{
		var random = new Random();
		var chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
		return new string(Enumerable.Repeat(chars, 15)
			.Select(s => s[random.Next(s.Length)]).ToArray());
	}

	public async Task<ADPosition?> GetPosition(AMOrderRequest order)
	{
		var position = await Db.Positions
			.Where(x => x.ExchangeId == Exchange.Id && x.Status == PositionStatus.Open && x.Type == order.PositionType)
			.FirstOrDefaultAsync(x => x.Symbol == order.Ticker);
		if (position == null) return null;

		return position;
	}

	public async Task<List<ADOrder>> GetOpenOrders(ADPosition position, OrderTriggerType triggerType) =>
		await Db.Orders
			.Where(x => x.ExchangeId == Exchange.Id && x.PositionId == position.Id && x.Status == OrderStatus.Open && x.TriggerType == triggerType)
			.ToListAsync();

	public async Task<ADOrder> AddOrder(ADPosition position, OrderTriggerType triggerType, OrderConditionType conditionType, decimal price, decimal quantity, PositionSizeType qtyType, bool closePosition, Guid? relatedTo = null)
	{
		var order = new ADOrder
		{
			Id = Guid.NewGuid(),
			UserId = position.UserId,
			BotId = position.BotId,
			ExchangeId = Exchange.Id,
			PositionId = position.Id,
			Symbol = position.Symbol,
			TriggerType = triggerType,
			ConditionType = conditionType,
			TriggerPrice = price,
			Quantity = quantity,
			QuantityType = qtyType,
			ClosePosition = closePosition,
			Status = OrderStatus.Open,
			RelatedTo = relatedTo,
			UpdatedDate = DateTime.MinValue,
			CreatedDate = DateTime.UtcNow
		};

		await Db.Orders.AddAsync(order);
		return order;
	}

	public async Task<ADPosition> AddPosition(ADBot bot, AMOrderRequest order, AMOrderResult result)
	{
		var position = new ADPosition
		{
			Id = Guid.NewGuid(),
			UserId = bot.UserId,
			BotId = bot.Id,
			ExchangeId = Exchange.Id,
			Symbol = order.Ticker,
			EntryPrice = result.EntryPrice,
			AvgEntryPrice = result.EntryPrice,
			Leverage = order.Leverage,
			Quantity = result.Quantity,
			CurrentQuantity = result.Quantity,
			Type = order.PositionType,
			Status = PositionStatus.Open,
			UpdatedDate = DateTime.UtcNow,
			CreatedDate = DateTime.UtcNow
		};

		await Db.Positions.AddAsync(position);

		return position;
	}

	public async Task ClosePosition(AMOrderRequest order)
	{
		var position = await Db.Positions
			.Where(x => x.ExchangeId == Exchange.Id && x.Status == PositionStatus.Open && x.Type == order.PositionType)
			.FirstOrDefaultAsync(x => x.Symbol == order.Ticker);
		if (position == null) return;

		position.Status = PositionStatus.Closed;
		position.UpdatedDate = DateTime.UtcNow;
	}

	public async Task ReducePosition(ADPosition position, AMOrderResult result, ADOrder? order = null)
	{
		if (order != null && !result.Success)
		{
			order?.Reject();
			return;
		}

		if (order == null)
		{
			await CancelOpenOrders(position);
			position.Close();
			return;
		}

		if (order.ClosePosition)
		{
			await CancelOpenOrders(position);
			position.Close();
		}

		order.Fill(result.Quantity);
		position.Reduce(result.Quantity);
	}

	public async Task CancelOpenOrders(ADPosition position)
	{
		if (position == null) return;

		await Db.Orders.Where(x => x.PositionId == position.Id && x.Status == OrderStatus.Open)
			.ForEachAsync(x =>
			{
				x.Status = OrderStatus.Cancelled;
				x.UpdatedDate = DateTime.UtcNow;
			});
	}

	public abstract Task<AMProviderResult> ExecuteOrder(ADBot bot, AMOrderRequest order);
	public abstract Task<AMProviderResult> ChangeTickersMarginType(List<string> tickers, MarginType type);

	public virtual void Dispose()
	{
		// Db.Dispose();
	}
}
