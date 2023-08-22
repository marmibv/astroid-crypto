using System.ComponentModel;
using System.Reflection;
using Astroid.Core;
using Astroid.Entity;
using Binance.Net.Enums;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

namespace Astroid.Providers;

public abstract class ExchangeProviderBase : IDisposable
{
	public IServiceProvider ServiceProvider { get; set; }
	protected AstroidDb Db { get; set; }
	protected ExchangeInfoStore ExchangeStore { get; set; }
	protected ADExchange Exchange { get; set; }
	public string CorrelationId { get; set; }

	protected ExchangeProviderBase() { }

	protected ExchangeProviderBase(IServiceProvider serviceProvider, ADExchange exchange)
	{
		ServiceProvider = serviceProvider;
		ExchangeStore = ServiceProvider.GetRequiredService<ExchangeInfoStore>();
		Exchange = exchange;
		CorrelationId = GenerateCorrelationId();
	}

	public async Task<int> GetEntryPointIndex(AMOrderBook orderBook, PositionSide pSide, LimitSettings settings)
	{
		var entryPoint = await GetEntryPoint(orderBook, pSide, settings);
		var i = pSide == PositionSide.Long ? await orderBook.GetGreatestAskPriceLessThan(entryPoint) : await orderBook.GetLeastBidPriceGreaterThan(entryPoint);

		if (i <= 0) throw new Exception("Could not find entry point out of order book.");

		return i;
	}

	public static async Task<decimal> GetEntryPoint(AMOrderBook orderBook, PositionSide pSide, LimitSettings settings)
	{
		if (settings.ComputationMethod == OrderBookComputationMethod.Code)
		{
			var pairs = pSide == PositionSide.Long ? await orderBook.GetAsks(settings.OrderBookDepth) : await orderBook.GetBids(settings.OrderBookDepth);
			var entries = pairs.Select(x => new AMOrderBookEntry { Price = x.Key, Quantity = x.Value }).ToList();

			var result = CodeExecutor.ExecuteComputationMethod(settings.Code, entries);
			if (!result.IsSuccess) throw new Exception(result.Message);

			return result.Data;
		}

		var prices = pSide == PositionSide.Long ? await orderBook.GetAskPrices(settings.OrderBookDepth) : await orderBook.GetBidPrices(settings.OrderBookDepth);

		var sDeviation = ComputeStandardDeviation(prices);
		var mean = prices.Average();

		return pSide == PositionSide.Long ? mean + (2 * sDeviation) : mean - (2 * sDeviation);
	}

	public static decimal ComputeStandardDeviation(IEnumerable<decimal> prices)
	{
		var mean = prices.Average();
		var squaredDifferences = prices.Select(p => Math.Pow((double)p - (double)mean, 2));
		var variance = squaredDifferences.Sum() / squaredDifferences.Count();
		var standardDeviation = Math.Sqrt(variance);

		return (decimal)standardDeviation;
	}

	public virtual void Context()
	{
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

	public abstract Task<AMProviderResult> ExecuteOrder(ADBot bot, AMOrderRequest order);
	public abstract Task<AMProviderResult> ChangeTickersMarginType(List<string> tickers, MarginType type);

	public virtual void Dispose()
	{
		// Db.Dispose();
	}
}
